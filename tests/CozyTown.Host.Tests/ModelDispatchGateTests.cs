using CozyTown.Game;
using CozyTown.Model;
using NUnit.Framework;

namespace CozyTown.Host.Tests;

[TestFixture]
public sealed class ModelDispatchGateTests
{
    [Test]
    public async Task PausedGateAcknowledgesOnlyDuringUnpausedTick()
    {
        using var gate = new ModelDispatchGate();
        int callbacks = 0, callbackThread = 0;
        Task dispatch = gate.EnqueueAsync("session", () => { callbacks++; callbackThread = Environment.CurrentManagedThreadId; return true; }, TimeSpan.FromSeconds(10));
        gate.Tick("session", true, false);
        Assert.That(dispatch.IsCompleted, Is.False);
        Assert.That(callbacks, Is.Zero);
        int tickThread = Environment.CurrentManagedThreadId;
        gate.Tick("session", false, false);
        await dispatch;
        Assert.That(callbacks, Is.EqualTo(1));
        Assert.That(callbackThread, Is.EqualTo(tickThread));
        gate.Tick("session", false, false);
        Assert.That(callbacks, Is.EqualTo(1));
    }

    [Test]
    public void RetiredSessionsAreRejectedWhilePausedWithoutTouchingWorld()
    {
        using var gate = new ModelDispatchGate();
        int callbacks = 0;
        Task dispatch = gate.EnqueueAsync("retired", () => { callbacks++; return true; }, TimeSpan.FromSeconds(10));
        gate.Tick("current", true, false);
        var error = Assert.ThrowsAsync<ModelException>(async () => await dispatch);
        Assert.That(error!.Code, Is.EqualTo("host_request_retired_before_send"));
        Assert.That(callbacks, Is.Zero);
    }

    [Test]
    public void TerminalRequestCannotStartTransport()
    {
        using var gate = new ModelDispatchGate();
        Task dispatch = gate.EnqueueAsync("session", () => false, TimeSpan.FromSeconds(10));
        gate.Tick("session", false, false);
        Assert.That(Assert.ThrowsAsync<ModelException>(async () => await dispatch)!.Code, Is.EqualTo("host_request_terminal_before_send"));
    }

    [Test]
    public async Task LogicalDeadlineExpiresWithoutAHostTick()
    {
        using var gate = new ModelDispatchGate();
        int callbacks = 0;
        Task dispatch = gate.EnqueueAsync("session", () => { callbacks++; return true; }, TimeSpan.FromMilliseconds(30));
        Task completed = await Task.WhenAny(dispatch, Task.Delay(5000));
        Assert.That(completed, Is.SameAs(dispatch));
        Assert.That(Assert.ThrowsAsync<ModelException>(async () => await dispatch)!.Code, Is.EqualTo("logical_wait_expired"));
        gate.Tick("session", false, false);
        Assert.That(callbacks, Is.Zero);
    }

    [Test]
    public void CallerCancellationCompletesPendingTaskAndPreventsLaterAcknowledgement()
    {
        using var gate = new ModelDispatchGate();
        using var canceled = new CancellationTokenSource();
        int callbacks = 0;
        Task dispatch = gate.EnqueueAsync("session", () => { callbacks++; return true; }, TimeSpan.FromSeconds(10), canceled.Token);
        canceled.Cancel();
        Assert.ThrowsAsync<TaskCanceledException>(async () => await dispatch);
        gate.Tick("session", false, false);
        Assert.That(callbacks, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void UnavailableHostOrStopReleasesAllPendingTasks(bool stop)
    {
        using var gate = new ModelDispatchGate();
        int callbacks = 0;
        Task[] tasks = Enumerable.Range(0, 4).Select(_ => gate.EnqueueAsync("session", () => { callbacks++; return true; }, TimeSpan.FromSeconds(10))).ToArray();
        if (stop) gate.Stop(); else gate.Tick("session", true, true);
        foreach (var task in tasks)
            Assert.That(Assert.ThrowsAsync<ModelException>(async () => await task)!.Code, Is.EqualTo("host_unavailable_before_send"));
        gate.Tick("session", false, false);
        Assert.That(callbacks, Is.Zero);
        if (stop)
            Assert.That(Assert.ThrowsAsync<ModelException>(async () => await gate.EnqueueAsync("session", () => true, TimeSpan.FromSeconds(10)))!.Code,
                Is.EqualTo("host_unavailable_before_send"));
    }

    [Test]
    public void CallbackFailureDoesNotLeaveOtherRequestsWaiting()
    {
        using var gate = new ModelDispatchGate();
        Task failed = gate.EnqueueAsync("session", () => throw new IOException("Injected callback failure."), TimeSpan.FromSeconds(10));
        Task healthy = gate.EnqueueAsync("session", () => true, TimeSpan.FromSeconds(10));
        gate.Tick("session", false, false);
        Assert.ThrowsAsync<IOException>(async () => await failed);
        Assert.DoesNotThrowAsync(async () => await healthy);
    }
}
