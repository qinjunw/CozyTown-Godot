using System.Text.Json;

namespace CozyTown.Core.Town;

public sealed record TownReplayHostState(double UnconsumedSeconds, TownPoint[]? PlayerRoute, int PlayerRouteCursor,
    string[] PauseReasons, Dictionary<string, string> FrozenRequests);

// The game and offline runner share host input rules for routes, pause reasons and frame accumulation.
public sealed class TownInputHost(TownContent content, TownWorld world, TownReplayHostState? initial = null)
{
    public const double StepSeconds = .05;
    private TownReplayHostState _state = ValidateInitial(content, world, initial);
    public TownReplayHostState State => TownJson.Clone(_state);
    public Action<TownTrace>? TraceCommitted { get; set; }

    private static TownReplayHostState ValidateInitial(TownContent content, TownWorld world, TownReplayHostState? initial)
    {
        if (initial is null) return new(0, null, 0, [], []);
        var state = TownJson.Clone(initial);
        if (!double.IsFinite(state.UnconsumedSeconds) || state.UnconsumedSeconds < 0 || state.UnconsumedSeconds > 1
            || state.PauseReasons is null || state.FrozenRequests is null || state.PauseReasons.Any(string.IsNullOrWhiteSpace)
            || state.PauseReasons.Distinct(StringComparer.Ordinal).Count() != state.PauseReasons.Length
            || state.PlayerRouteCursor < 0 || state.PlayerRouteCursor > (state.PlayerRoute?.Length ?? 0) || state.PlayerRoute?.Length > 32768)
            throw new InvalidDataException("Initial host clock, pause reasons or route cursor are invalid.");
        TownPoint previous = world.State.PlayerPosition;
        foreach (var point in (state.PlayerRoute ?? []).Skip(state.PlayerRouteCursor))
        {
            if (!content.CanMove(previous, point)) throw new InvalidDataException("Initial host route is not walkable.");
            previous = point;
        }
        return state;
    }

    public void RouteTo(TownPoint target) => Apply("host_route", new { target });
    public void CancelRoute(string reason) => Apply("host_cancel_route", new { reason });
    public void Move(double seconds, TownPoint direction) => Apply("host_move", new { seconds, direction });
    public void Pause(string reason, bool paused) => Apply("host_pause", new { reason, paused });
    public void AcceptElapsed(double elapsed) => Apply("host_elapsed", new { elapsed });
    public void TakeStep() => Apply("host_step", new { });
    public void FreezeRequest(string logicalRequestId, string requestJson) => Apply("host_freeze_request", new { logicalRequestId, requestJson });
    public void PruneTerminalRequests() => Apply("host_prune_requests", new { });

    public void Apply(string command, object input) => ApplyJson(command, TownJson.Serialize(input));

    public void ApplyJson(string command, string inputJson)
    {
        var queries = new List<TownSpatialQuery>();
        var previous = world.TraceCommitted;
        world.TraceCommitted = trace => queries.AddRange(trace.Queries);
        try
        {
            using var document = JsonDocument.Parse(inputJson);
            var input = document.RootElement;
            T Read<T>(string key) => TownJson.Deserialize<T>(input.GetProperty(key).GetRawText());
            switch (command)
            {
                case "host_route": _state = _state with { PlayerRoute = content.FindRoute(world.State.PlayerPosition, Read<TownPoint>("target"))?.ToArray(), PlayerRouteCursor = 0 }; break;
                case "host_cancel_route": _ = Read<string>("reason"); _state = _state with { PlayerRoute = null, PlayerRouteCursor = 0 }; break;
                case "host_pause":
                    var reasons = _state.PauseReasons.ToHashSet(StringComparer.Ordinal);
                    if (Read<bool>("paused")) reasons.Add(Read<string>("reason")); else reasons.Remove(Read<string>("reason"));
                    _state = _state with { PauseReasons = reasons.Order(StringComparer.Ordinal).ToArray() }; break;
                case "host_elapsed":
                    double elapsed = Read<double>("elapsed");
                    if (!double.IsFinite(elapsed) || elapsed < 0 || elapsed > 86400) throw new InvalidDataException("Host elapsed seconds are outside the supported range.");
                    _state = _state with { UnconsumedSeconds = _state.UnconsumedSeconds + Math.Min(elapsed, .25) }; break;
                case "host_step":
                    if (_state.UnconsumedSeconds < StepSeconds) throw new InvalidDataException("Host step has no accepted time.");
                    _state = _state with { UnconsumedSeconds = _state.UnconsumedSeconds - StepSeconds }; break;
                case "host_move": MoveCore(Read<double>("seconds"), Read<TownPoint>("direction")); break;
                case "host_prune_requests":
                    var terminal = world.State.TerminalRequestIds.ToHashSet(StringComparer.Ordinal);
                    foreach (string key in _state.FrozenRequests.Keys.Where(terminal.Contains).ToArray()) _state.FrozenRequests.Remove(key);
                    break;
                case "host_freeze_request":
                    string id = Read<string>("logicalRequestId"), json = Read<string>("requestJson");
                    var pending = world.State.PendingDecisions.Single(p => p.Request.LogicalRequestId == id);
                    using (var request = JsonDocument.Parse(json))
                    {
                        var users = request.RootElement.GetProperty("inputJson").GetString();
                        using var provider = JsonDocument.Parse(users!);
                        if (!provider.RootElement.GetProperty("messages").EnumerateArray().Any(m => m.GetProperty("role").GetString() == "user" && m.GetProperty("content").GetString() == pending.Request.InputJson))
                            throw new InvalidDataException("Frozen provider input differs from the pending decision.");
                    }
                    _state.FrozenRequests[id] = json; break;
                default: throw new InvalidDataException("Unsupported host input: " + command);
            }
        }
        finally { world.TraceCommitted = previous; }
        TraceCommitted?.Invoke(new(command, inputJson, queries.ToArray(), command == "host_move" ? world.State : null, State));
    }

    private void MoveCore(double seconds, TownPoint direction)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > 1 || !double.IsFinite(direction.X) || !double.IsFinite(direction.Y)) throw new InvalidDataException("Player movement input is invalid.");
        if (_state.PauseReasons.Length > 0) return;
        var current = world.State.PlayerPosition;
        double length = direction.DistanceTo(new(0, 0));
        if (length > 0)
        {
            _state = _state with { PlayerRoute = null, PlayerRouteCursor = 0 };
            // Godot's input normalization uses float components.
            float x = (float)(direction.X / length) * (float)(seconds * 62), y = (float)(direction.Y / length) * (float)(seconds * 62);
            if (!world.SetPlayerPosition(new(current.X + x, current.Y + y)))
            {
                if (Math.Abs(x) > .001) world.SetPlayerPosition(new(current.X + x, current.Y));
                if (Math.Abs(y) > .001) world.SetPlayerPosition(new(current.X, current.Y + y));
            }
            return;
        }
        if (_state.PlayerRoute is not { } route || _state.PlayerRouteCursor >= route.Length) return;
        TownPoint target = route[_state.PlayerRouteCursor];
        double distance = current.DistanceTo(target), amount = Math.Min(distance, seconds * 62);
        if (distance < .01) { _state = _state with { PlayerRouteCursor = _state.PlayerRouteCursor + 1 }; return; }
        var next = new TownPoint(current.X + (target.X - current.X) / distance * amount, current.Y + (target.Y - current.Y) / distance * amount);
        if (!world.SetPlayerPosition(next)) _state = _state with { PlayerRoute = null, PlayerRouteCursor = 0 };
        else if (amount >= distance) _state = _state with { PlayerRouteCursor = _state.PlayerRouteCursor + 1 };
    }
}
