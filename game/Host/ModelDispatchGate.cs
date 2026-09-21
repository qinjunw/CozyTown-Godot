using System.Diagnostics;
using CozyTown.Model;

namespace CozyTown.Game;

// Only Tick invokes world callbacks; cancellation and deadlines complete tasks without touching the world.
public sealed class ModelDispatchGate : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<PendingDispatch> _pending = new();
    private bool _stopped;

    public Task EnqueueAsync(string sessionId, Func<bool> acknowledge, TimeSpan deadlineRemaining,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("Dispatch session identity is required.", nameof(sessionId));
        ArgumentNullException.ThrowIfNull(acknowledge);
        if (deadlineRemaining <= TimeSpan.Zero || deadlineRemaining > TimeSpan.FromSeconds(60))
            return Task.FromException(new ModelException("logical_wait_expired"));
        var pending = new PendingDispatch(sessionId, acknowledge, deadlineRemaining, cancellationToken);
        lock (_gate)
        {
            if (_stopped) pending.Reject("host_unavailable_before_send");
            else _pending.Enqueue(pending);
        }
        return WaitAsync(pending, cancellationToken);
    }

    public void Tick(string currentSessionId, bool paused, bool unavailable)
    {
        lock (_gate)
        {
            int count = _pending.Count;
            for (int index = 0; index < count; index++)
            {
                if (!_pending.TryDequeue(out var pending)) break;
                if (pending.Completed) continue;
                if (_stopped || unavailable) pending.Reject("host_unavailable_before_send");
                else if (pending.SessionId != currentSessionId) pending.Reject("host_request_retired_before_send");
                else if (paused) _pending.Enqueue(pending);
                else pending.Acknowledge();
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            while (_pending.TryDequeue(out var pending)) pending.Reject("host_unavailable_before_send");
        }
    }

    public void Dispose() => Stop();

    private static async Task WaitAsync(PendingDispatch pending, CancellationToken cancellationToken)
    {
        TimeSpan remaining = pending.Remaining;
        if (remaining <= TimeSpan.Zero) pending.CancelOrExpire();
        using var timeout = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        using var registration = linked.Token.Register(() => pending.CancelOrExpire());
        await pending.Completion.Task.ConfigureAwait(false);
    }

    private sealed class PendingDispatch(string sessionId, Func<bool> acknowledge, TimeSpan remaining, CancellationToken cancellationToken)
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private int _state;
        public string SessionId { get; } = sessionId;
        public TimeSpan Remaining => remaining - Stopwatch.GetElapsedTime(_started);
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Completed => Volatile.Read(ref _state) == 2;

        public void Reject(string code)
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) == 0) Completion.TrySetException(new ModelException(code));
        }

        public void CancelOrExpire()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) != 0) return;
            if (cancellationToken.IsCancellationRequested) Completion.TrySetCanceled(cancellationToken);
            else Completion.TrySetException(new ModelException("logical_wait_expired"));
        }

        public void Acknowledge()
        {
            if (cancellationToken.IsCancellationRequested || Stopwatch.GetElapsedTime(_started) >= remaining) { CancelOrExpire(); return; }
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;
            try
            {
                if (!acknowledge()) throw new ModelException("host_request_terminal_before_send");
                Completion.TrySetResult();
            }
            catch (Exception error) { Completion.TrySetException(error); }
            finally { Volatile.Write(ref _state, 2); }
        }
    }
}
