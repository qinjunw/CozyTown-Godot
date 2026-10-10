using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;

namespace CozyTown.Core.Town;

public sealed record TownReplayPackage(int SchemaVersion, string RulesVersion, string NumericVersion,
    TownManifest Manifest, TownDefinitions Definitions, TownSnapshot InitialSnapshot, string SessionId,
    TownTrace[] Boundaries, int BoundaryCount, TownReplayHostState? InitialHost = null, TownReplayParent? Parent = null,
    TownReplayBudgetState? InitialBudget = null, TownReplayBudgetProfile? BudgetProfile = null, string? Interruption = null,
    bool UnsealedPrefix = false, TownSceneReplayDependencies? Scene = null);
public sealed record TownSceneReplayDependencies(string Version, string Scenario, string EngineVersion,
    string ScenePath, string MapPath, string ContentFingerprint, string GeometryFingerprint, double BodyRadius,
    TownSpatialConfiguration Query, string SimulationSourceDigest, string BuildVersion, string AssemblySha256,
    string EngineBuild, string PhysicsBackend);
public sealed record TownReplayParent(TownReplayPackage Prefix, TownSnapshot SavedSnapshot, TownReplayHostState? SavedHost);
public sealed record TownReplayReport(bool Passed, string Code, int Boundary, string? Field,
    string? Expected, string? Actual, string RulesVersion, string NumericVersion, string? FinalStateJson = null,
    string? Command = null, string? InputJson = null, string Mode = "core_fact_replay");

public sealed class TownRecording
{
    private TownReplayPackage _start;
    private readonly List<TownTrace> _boundaries = [];
    private readonly object _gate = new();
    public const long DefaultMaximumBytes = 448L * 1024 * 1024;
    private long _estimatedBytes;
    private string? _interruption;
    private JsonNode? _previousWorld, _previousHost;
    private int _previousWorldBoundary, _previousHostBoundary;
    public Action<TownTrace>? BoundaryCommitted { get; set; }
    public TownRecording(TownContent content, TownWorld world, TownInputHost? host = null, TownReplayParent? parent = null,
        long maximumBytes = DefaultMaximumBytes)
    {
        _start = new(1, TownWorld.RulesVersion, TownWorld.NumericVersion, content.Manifest, content.Definitions,
            world.CaptureSnapshot(), world.SessionId, [], 0, host?.State, parent);
        _estimatedBytes = JsonSerializer.SerializeToUtf8Bytes(_start, TownJson.Options).LongLength;
        void Append(TownTrace trace)
        {
            lock (_gate)
            {
                trace = trace with { Sequence = _boundaries.Count + 1, InputFingerprint = TownContent.Hash(trace.Command + "\n" + trace.InputJson) };
                JsonNode? worldCheckpoint = trace.CheckpointData is null ? null : TownCheckpointEncoding.Decode(trace.CheckpointData);
                JsonNode? hostCheckpoint = trace.HostCheckpoint is null ? null : JsonSerializer.SerializeToNode(trace.HostCheckpoint, TownJson.Options);
                if (worldCheckpoint is not null && _previousWorld is not null)
                    trace = trace with { CheckpointData = null, CheckpointDeltaData = TownCheckpointEncoding.Difference(_previousWorld, worldCheckpoint), CheckpointBaseBoundary = _previousWorldBoundary };
                if (hostCheckpoint is not null && _previousHost is not null)
                    trace = trace with { HostCheckpoint = null, HostCheckpointDeltaData = TownCheckpointEncoding.Difference(_previousHost, hostCheckpoint), HostCheckpointBaseBoundary = _previousHostBoundary };
                if (_interruption is not null) throw new InvalidDataException("Recording has stopped: " + _interruption);
                // Indented package wrappers and journal envelopes are covered by this per-boundary allowance.
                long size = JsonSerializer.SerializeToUtf8Bytes(trace, TownJson.Options).LongLength + 1024;
                if (_estimatedBytes + size > maximumBytes)
                {
                    _interruption = "recording_capacity_reached";
                    throw new InvalidDataException("Recording capacity reached; the last complete prefix is retained. World updates and model dispatch must stop.");
                }
                try { BoundaryCommitted?.Invoke(trace); }
                catch { _interruption = "recording_write_failed"; throw; }
                _boundaries.Add(trace);
                _estimatedBytes += size;
                if (worldCheckpoint is not null) { _previousWorld = worldCheckpoint; _previousWorldBoundary = trace.Sequence; }
                if (hostCheckpoint is not null) { _previousHost = hostCheckpoint; _previousHostBoundary = trace.Sequence; }
            }
        }
        _append = Append;
        world.TraceCommitted = trace => Append(trace with { HostCheckpoint = host?.State });
        if (host is not null) host.TraceCommitted = Append;
    }
    private readonly Action<TownTrace> _append;
    public void RecordExternal(string command, object input) => _append(new(command, TownJson.Serialize(input), [], null!));
    public void SetSceneDependencies(TownSceneReplayDependencies scene)
    {
        lock (_gate)
        {
            if (_boundaries.Count != 0) throw new InvalidOperationException("Scene dependencies must precede replay inputs.");
            _start = _start with { Scene = TownJson.Clone(scene) };
        }
    }
    public void SetInitialBudget(TownReplayBudgetState budget, TownReplayBudgetProfile? profile = null)
    {
        lock (_gate)
        {
            if (_boundaries.Count != 0) throw new InvalidOperationException("Initial budget must precede replay inputs.");
            _start = _start with { InitialBudget = TownJson.Clone(budget), BudgetProfile = TownJson.Clone(profile ?? TownReplayBudgetProfile.FirstSlice) };
        }
    }
    public TownReplayPackage Complete() { lock (_gate) return _start with { Boundaries = _boundaries.ToArray(), BoundaryCount = _boundaries.Count, Interruption = _interruption }; }
    public void WithSnapshot(Action<TownReplayPackage> capture) { lock (_gate) capture(Complete()); }
}

public static class TownReplay
{
    public const double CoordinateTolerance = .00001;
    private static readonly JsonSerializerOptions PackageJson = new(TownJson.Options) { AllowDuplicateProperties = false };

    public static TownReplayPackage ReadPackage(string path)
    {
        if (Directory.Exists(path))
        {
            var package = ReadJson(Path.Combine(path, "replay.json"));
            using var file = new FileStream(Path.Combine(path, "events.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return ReadJournal(package, file);
        }
        else if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(path);
            var entries = archive.Entries.Where(e => e.FullName == "replay.json").ToArray();
            if (entries.Length != 1) throw new InvalidDataException("Recording lacks replay.json; legacy records do not contain the required inputs and checkpoints.");
            if (entries[0].Length > 512 * 1024 * 1024) throw new InvalidDataException("Replay package exceeds 512 MiB.");
            TownReplayPackage package;
            using (var input = entries[0].Open()) package = ReadJson(input);
            var journals = archive.Entries.Where(e => e.FullName == "events.jsonl").ToArray();
            if (journals.Length > 1) throw new InvalidDataException("Replay export contains duplicate journals.");
            if (journals.Length == 1)
            {
                if (journals[0].Length > 512L * 1024 * 1024) throw new InvalidDataException("Replay journal exceeds 512 MiB.");
                using var journal = journals[0].Open();
                return ReadJournal(package, journal);
            }
            return package;
        }
        else return ReadJson(path);
    }

    private static TownReplayPackage ReadJournal(TownReplayPackage package, Stream stream)
    {
        if (stream.CanSeek && stream.Length > 512L * 1024 * 1024) throw new InvalidDataException("Replay journal exceeds 512 MiB.");
        using var reader = new StreamReader(stream, leaveOpen: true);
        var boundaries = new List<TownTrace>();
        long sequence = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { AllowDuplicateProperties = false });
            var entry = document.RootElement;
            if (entry.GetProperty("sequence").GetInt64() != ++sequence) throw new InvalidDataException("Run journal has a sequence gap at " + sequence + ".");
            if (entry.GetProperty("kind").GetString() == "replay_boundary")
            {
                var data = entry.GetProperty("data");
                int index = boundaries.Count;
                if (index < package.BoundaryCount)
                {
                    if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(package.Boundaries[index], TownJson.Options), data))
                        throw new InvalidDataException("Run journal differs from its sealed replay prefix at boundary " + (index + 1) + ".");
                    boundaries.Add(package.Boundaries[index]);
                }
                else boundaries.Add(TownJson.Deserialize<TownTrace>(data.GetRawText()));
            }
        }
        if (package.BoundaryCount != package.Boundaries.Length || boundaries.Count < package.BoundaryCount) throw new InvalidDataException("Run journal is missing the sealed replay prefix.");
        return package with { Boundaries = boundaries.ToArray(), BoundaryCount = boundaries.Count, UnsealedPrefix = boundaries.Count > package.BoundaryCount };
    }

    private static TownReplayPackage ReadJson(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > 512L * 1024 * 1024) throw new InvalidDataException("Replay package exceeds 512 MiB.");
        return ReadJson(file);
    }

    private static TownReplayPackage ReadJson(Stream stream) => JsonSerializer.Deserialize<TownReplayPackage>(stream, PackageJson)
        ?? throw new InvalidDataException("Replay package root cannot be null.");

    public static TownReplayReport Run(TownReplayPackage package, TownReplaySceneAdapter? scene = null) => Run(package, 0, scene);

    private static TownReplayReport Run(TownReplayPackage package, int depth, TownReplaySceneAdapter? scene)
    {
        int boundary = 0;
        string? command = null, relatedInput = null;
        TownReplayReport Failure(string code, string? field, string? expected, string? actual) =>
            new(false, code, boundary, field, expected, actual, package.RulesVersion, package.NumericVersion, Command: command, InputJson: relatedInput,
                Mode: scene is null ? "core_fact_replay" : "godot_scene_replay");
        try
        {
            scene?.ValidateDependencies?.Invoke(package);
            if (depth > 32) return Failure("invalid_parent_prefix", "parent", "at most 32 fork ancestors", depth.ToString());
            if (package.SchemaVersion != 1) return Failure("unsupported_recording", "schemaVersion", "1", package.SchemaVersion.ToString());
            if (package.RulesVersion != TownWorld.RulesVersion || package.NumericVersion != TownWorld.NumericVersion)
                return Failure("incompatible_version", "simulation", TownWorld.RulesVersion + "/" + TownWorld.NumericVersion, package.RulesVersion + "/" + package.NumericVersion);
            if (package.Boundaries.Length != package.BoundaryCount) return Failure("record_gap", "boundaryCount", package.BoundaryCount.ToString(), package.Boundaries.Length.ToString());
            var content = TownContent.Create(package.Manifest, package.Definitions);
            if (package.InitialSnapshot.State.ParentBranchId is not null)
            {
                if (package.Parent is null) return Failure("missing_parent_prefix", "parent", package.InitialSnapshot.State.ParentBranchId, null);
                var parent = package.Parent;
                var result = Run(parent.Prefix, depth + 1, scene);
                if (!result.Passed) return result with { Code = "invalid_parent_prefix", Field = "parent." + result.Field, FinalStateJson = null };
                Compare(JsonNode.Parse(result.FinalStateJson!), JsonSerializer.SerializeToNode(parent.SavedSnapshot.State, TownJson.Options), "parent.savedState");
                if (parent.SavedSnapshot.SnapshotId != package.InitialSnapshot.State.SourceSnapshotId
                    || parent.SavedSnapshot.State.BranchId != package.InitialSnapshot.State.ParentBranchId
                    || parent.SavedSnapshot.State.Sequence != package.InitialSnapshot.State.ForkSequence)
                    return Failure("invalid_parent_prefix", "parent.fork", parent.SavedSnapshot.SnapshotId, package.InitialSnapshot.State.SourceSnapshotId);
                var restored = TownWorld.RestoreReplayFork(content, parent.SavedSnapshot, package.InitialSnapshot.State.BranchId, package.SessionId);
                Compare(JsonSerializer.SerializeToNode(package.InitialSnapshot.State, TownJson.Options), JsonSerializer.SerializeToNode(restored.State, TownJson.Options), "parent.restoredState");
                if (parent.SavedHost is not null && package.InitialHost is not null)
                {
                    Compare(JsonSerializer.SerializeToNode(parent.SavedHost with { PauseReasons = package.InitialHost.PauseReasons }, TownJson.Options), JsonSerializer.SerializeToNode(package.InitialHost, TownJson.Options), "parent.host");
                }
            }
            else if (package.Parent is not null) return Failure("invalid_parent_prefix", "parent", "no parent for root run", "unexpected parent");
            TownTrace? expectedTrace = null;
            int query = 0;
            bool Motion(string id, TownPoint from, TownPoint to)
            {
                if (expectedTrace is null || query >= expectedTrace.Queries.Length) throw new ReplayMismatch("queries[" + query + "]", "recorded query", "unexpected query");
                var expected = expectedTrace.Queries[query++];
                // Compare query identity before invoking physics; a reordered query must not act on the wrong body.
                var actual = new TownSpatialQuery(id, "body-motion-v1", from, to, TownContent.BodyRadius, expected.Clear, expected.Evidence);
                Compare(JsonSerializer.SerializeToNode(expected, TownJson.Options), JsonSerializer.SerializeToNode(actual, TownJson.Options), "queries[" + (query - 1) + "]");
                if (scene is null) return expected.Clear;
                var result = scene.Query(id, from, to, TownContent.BodyRadius);
                actual = actual with { Clear = result.Clear, Evidence = expected.Evidence is null ? null : result };
                Compare(JsonSerializer.SerializeToNode(expected, TownJson.Options), JsonSerializer.SerializeToNode(actual, TownJson.Options), "queries[" + (query - 1) + "]");
                return result.Clear;
            }
            var world = TownWorld.OpenReplay(content, package.InitialSnapshot, package.SessionId, Motion);
            scene?.ValidateBodies?.Invoke(world.State, 0);
            var host = new TownInputHost(content, world, package.InitialHost);
            if (package.InitialBudget is not null && package.InitialBudget.BudgetId != package.InitialSnapshot.State.BudgetId)
                return Failure("invalid_recording", "budget.budgetId", package.InitialSnapshot.State.BudgetId, package.InitialBudget.BudgetId);
            var budget = package.InitialBudget is null ? null : new TownReplayBudget(package.InitialBudget, package.BudgetProfile);
            var arrivals = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var completions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var mailbox = new HashSet<string>(StringComparer.Ordinal);
            var playerArrivals = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var checkpoints = new TownCheckpointCursor();
            foreach (TownTrace trace in package.Boundaries)
            {
                command = trace.Command; relatedInput = trace.InputJson;
                boundary++;
                if (trace.Sequence != boundary) throw new ReplayMismatch("sequence", boundary.ToString(), trace.Sequence.ToString());
                expectedTrace = trace; query = 0;
                if (trace.Command.StartsWith("host_", StringComparison.Ordinal)) host.ApplyJson(trace.Command, trace.InputJson);
                else if (trace.Command.StartsWith("budget_", StringComparison.Ordinal))
                {
                    if (budget is null) throw new InvalidDataException("Budget inputs require the recorded initial budget view.");
                    using var doc = JsonDocument.Parse(trace.InputJson);
                    var input = doc.RootElement;
                    T Read<T>(string field) => TownJson.Deserialize<T>(input.GetProperty(field).GetRawText());
                    switch (trace.Command)
                    {
                        case "budget_reserve": budget.Reserve(Read<string>("stage"), Read<string>("logicalRequestId"),
                            input.GetProperty("attemptId").ValueKind == JsonValueKind.Null ? "" : Read<string>("attemptId"),
                            Read<bool>("recovery"), Read<string>("admission"), Read<TownReplayBudgetState>("after")); break;
                        case "budget_external": budget.ApplyExternalState(Read<TownReplayBudgetState>("before"), Read<TownReplayBudgetState>("after")); break;
                        case "budget_settle": budget.Settle(Read<string>("attemptId"), Read<long>("promptTokens"), Read<long>("completionTokens"), Read<TownReplayBudgetState>("after")); break;
                        case "budget_unknown": budget.MarkUnknown(Read<string>("attemptId"), Read<string>("reason"), Read<bool>("generationEnded"), Read<bool>("stopFurther"), Read<TownReplayBudgetState>("after")); break;
                        case "budget_stop": budget.Stop(Read<string>("reason"), Read<TownReplayBudgetState>("after")); break;
                        default: throw new InvalidDataException("Unsupported budget input: " + trace.Command);
                    }
                }
                else if (trace.Command.StartsWith("model_", StringComparison.Ordinal))
                {
                    if (budget is null) throw new InvalidDataException("Model inputs require the recorded initial budget view.");
                    using var doc = JsonDocument.Parse(trace.InputJson);
                    var input = doc.RootElement;
                    string attempt = input.GetProperty("dispatch").GetProperty("attemptId").GetString()!;
                    if (trace.Command == "model_response_arrived")
                    {
                        if (!arrivals.TryAdd(attempt, input.Clone())) throw new ReplayMismatch("model.arrival", "unique response", attempt);
                        if (playerArrivals.TryGetValue(attempt, out var worldArrival)) ValidatePlayerArrival(worldArrival, input);
                    }
                    else if (trace.Command == "model_completion_persisted")
                    {
                        if (input.GetProperty("arrivedAtUtc").ValueKind != JsonValueKind.Null && !arrivals.ContainsKey(attempt))
                            throw new ReplayMismatch("model.arrival", "body arrival before persistence", "missing");
                        if (!completions.TryAdd(attempt, input.Clone())) throw new ReplayMismatch("model.completion", "unique persistence", attempt);
                        if (arrivals.TryGetValue(attempt, out var arrival))
                        {
                            Compare(JsonNode.Parse(arrival.GetProperty("dispatch").GetRawText()), JsonNode.Parse(input.GetProperty("dispatch").GetRawText()), "model.dispatch");
                            Compare(JsonNode.Parse(arrival.GetProperty("elapsed").GetRawText()), JsonNode.Parse(input.GetProperty("logicalElapsedToArrival").GetRawText()), "model.logicalElapsedToArrival");
                            if (input.GetProperty("candidateJson").ValueKind == JsonValueKind.String && arrival.GetProperty("responseBody").ValueKind == JsonValueKind.String)
                            {
                                using var body = JsonDocument.Parse(arrival.GetProperty("responseBody").GetString()!);
                                var candidate = body.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                                if (candidate != input.GetProperty("candidateJson").GetString()) throw new ReplayMismatch("model.candidateJson", candidate, input.GetProperty("candidateJson").GetString());
                            }
                        }
                    }
                    else if (trace.Command == "model_mailbox_enqueued" || trace.Command == "model_mailbox_entered")
                    {
                        if (!completions.ContainsKey(attempt)) throw new ReplayMismatch("model.mailbox", "durable completion", "missing");
                        Compare(JsonNode.Parse(completions[attempt].GetProperty("dispatch").GetRawText()), JsonNode.Parse(input.GetProperty("dispatch").GetRawText()), "model.mailboxDispatch");
                        if (trace.Command == "model_mailbox_entered") mailbox.Add(attempt);
                    }
                    else throw new InvalidDataException("Unsupported model boundary: " + trace.Command);
                }
                else
                {
                    if (trace.Command == "player_dialogue_arrival")
                    {
                        using var playerInput = JsonDocument.Parse(trace.InputJson);
                        var input = playerInput.RootElement;
                        string attempt = input.GetProperty("attemptId").GetString()!;
                        playerArrivals[attempt] = input.Clone();
                        if (arrivals.TryGetValue(attempt, out var arrival))
                            ValidatePlayerArrival(input, arrival);
                    }
                    if (trace.Command == "player_dialogue_result")
                    {
                        using var playerInput = JsonDocument.Parse(trace.InputJson);
                        var result = playerInput.RootElement.GetProperty("result");
                        if (result.GetProperty("source").GetString() == "real_model")
                        {
                            string attempt = result.GetProperty("attemptId").GetString()!;
                            if (!mailbox.Contains(attempt)) throw new ReplayMismatch("model.mailbox", "entered completion", attempt);
                            var completion = completions[attempt];
                            ValidatePlayerBinding(result, completion.GetProperty("dispatch"));
                            if (result.GetProperty("sessionId").GetString() == world.SessionId && completion.GetProperty("arrivedAtUtc").ValueKind != JsonValueKind.Null && !playerArrivals.ContainsKey(attempt))
                                throw new ReplayMismatch("model.playerArrival", "world arrival input before result", "missing");
                            if (completion.GetProperty("logicalElapsedToArrival").ValueKind != JsonValueKind.Null)
                                Compare(JsonSerializer.SerializeToNode(PlayerArrivalSeconds(completion.GetProperty("dispatch"), completion.GetProperty("logicalElapsedToArrival"))),
                                    JsonNode.Parse(result.GetProperty("receivedAfterSeconds").GetRawText()), "model.playerArrivalSeconds");
                            string? error = completion.GetProperty("failureCode").GetString(), text = null;
                            if (error is null)
                            {
                                try
                                {
                                    text = TownJson.Deserialize<ReplayPlayerText>(completion.GetProperty("candidateJson").GetString() ?? "").Text;
                                    if (string.IsNullOrWhiteSpace(text) || text.Length > 160 || text.Any(char.IsControl)) throw new InvalidDataException("Invalid player text.");
                                }
                                catch (Exception invalid) when (invalid is JsonException or InvalidDataException)
                                { text = null; error = "player_text_schema_invalid"; }
                            }
                            Compare(JsonSerializer.SerializeToNode(text), JsonNode.Parse(result.GetProperty("text").GetRawText()), "model.playerText");
                            Compare(JsonSerializer.SerializeToNode(error), JsonNode.Parse(result.GetProperty("error").GetRawText()), "model.playerError");
                        }
                    }
                    if (trace.Command == "result")
                    {
                        using var input = JsonDocument.Parse(trace.InputJson);
                        var result = input.RootElement.GetProperty("result");
                        if (result.GetProperty("source").GetString() == "real_model" && result.GetProperty("sessionId").GetString() == world.SessionId)
                        {
                            string attempt = result.GetProperty("attemptId").GetString()!;
                            if (!mailbox.Contains(attempt)) throw new ReplayMismatch("model.mailbox", "entered completion", attempt);
                            var completion = completions[attempt];
                            if (completion.GetProperty("failureCode").ValueKind == JsonValueKind.Null && result.GetProperty("error").ValueKind == JsonValueKind.Null)
                                Compare(JsonNode.Parse(completion.GetProperty("candidateJson").GetString()!), JsonNode.Parse(result.GetProperty("candidate").GetRawText()), "model.worldCandidate");
                        }
                    }
                    Execute(world, trace);
                }
                if (query != trace.Queries.Length) throw new ReplayMismatch("queries", trace.Queries.Length.ToString(), query.ToString());
                var checkpoint = checkpoints.ReadWorld(trace);
                bool needsWorld = !trace.Command.StartsWith("model_", StringComparison.Ordinal) && !trace.Command.StartsWith("budget_", StringComparison.Ordinal)
                    && (!trace.Command.StartsWith("host_", StringComparison.Ordinal) || trace.Command is "host_move" or "host_flush_paused_elapsed");
                if (needsWorld && checkpoint is null) throw new InvalidDataException("World-changing input lacks its complete checkpoint.");
                var hostCheckpoint = checkpoints.ReadHost(trace);
                if (trace.Command.StartsWith("host_", StringComparison.Ordinal) && hostCheckpoint is null) throw new InvalidDataException("Host input lacks its complete checkpoint.");
                if (checkpoint is not null) Compare(checkpoint, JsonSerializer.SerializeToNode(world.State, TownJson.Options), "state");
                if (hostCheckpoint is not null) Compare(hostCheckpoint, JsonSerializer.SerializeToNode(host.State, TownJson.Options), "host");
                scene?.ValidateBodies?.Invoke(world.State, boundary);
                string inputFingerprint = TownContent.Hash(trace.Command + "\n" + trace.InputJson);
                if (trace.InputFingerprint != inputFingerprint) throw new ReplayMismatch("inputFingerprint", trace.InputFingerprint, inputFingerprint);
            }
            if (package.Interruption is not null) return Failure("recording_interrupted", "lastCompleteBoundary", boundary.ToString(), package.Interruption);
            if (package.UnsealedPrefix) return Failure("unsealed_recording", "lastCompleteBoundary", boundary.ToString(), "journal extends beyond the sealed package") with { FinalStateJson = TownJson.Serialize(world.State) };
            return new(true, "passed", boundary, null, null, null, package.RulesVersion, package.NumericVersion, TownJson.Serialize(world.State),
                Mode: scene is null ? "core_fact_replay" : "godot_scene_replay");
        }
        catch (ReplayMismatch error) { return Failure("divergence", error.Field, error.Expected, error.Actual); }
        catch (InvalidDataException error) when (error.Data.Contains("field"))
        { return Failure(error.Data["code"]?.ToString() ?? "divergence", error.Data["field"]?.ToString(), error.Data["expected"]?.ToString(), error.Data["actual"]?.ToString()); }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or ArgumentException or KeyNotFoundException or NullReferenceException or FormatException or OverflowException)
        { return Failure("invalid_recording", "input", "complete compatible recording", error.Message); }
    }

    private static void Execute(TownWorld world, TownTrace trace)
    {
        using var document = JsonDocument.Parse(trace.InputJson);
        JsonElement input = document.RootElement;
        T Read<T>(string field) => TownJson.Deserialize<T>(input.GetProperty(field).GetRawText());
        switch (trace.Command)
        {
            case "advance": world.Advance(Read<double>("worldMinutes"), Read<double>("realSeconds"), Read<bool>("paused")); break;
            case "move": world.SetPlayerPosition(Read<TownPoint>("position")); break;
            case "talk": world.TalkToResident(Read<string>("residentId")); break;
            case "player_dialogue_open": world.OpenPlayerDialogue(Read<string>("residentId")); break;
            case "player_dialogue_request": world.RequestPlayerDialogue(Read<string>("playerSessionId")); break;
            case "player_dialogue_dispatch": world.MarkPlayerDialogueDispatched(Read<string>("playerSessionId"), Read<string>("logicalRequestId"), Read<string>("attemptId")); break;
            case "player_dialogue_arrival": world.MarkPlayerDialogueResponseArrived(Read<string>("sessionId"), Read<string>("playerSessionId"), Read<string>("logicalRequestId"), Read<string>("attemptId"), Read<double>("receivedAfterSeconds")); break;
            case "player_dialogue_result": world.ReceivePlayerDialogueResult(Read<PlayerDialogueResult>("result")); break;
            case "player_dialogue_display": world.DisplayPlayerDialogue(Read<string>("playerSessionId"), Read<bool>("allowed")); break;
            case "player_dialogue_close": world.ClosePlayerDialogue(Read<string>("playerSessionId")); break;
            case "player_dialogue_retire": world.ApplyPlayerDialogueRetirement(Read<string>("playerSessionId"), Read<string>("reason")); break;
            case "decisions": world.GetDecisionRequests(); break;
            case "dispatch": world.MarkDispatched(Read<string>("logicalRequestId"), Read<string>("attemptId")); break;
            case "arrival": world.MarkResponseArrived(Read<string>("sessionId"), Read<string>("logicalRequestId"), Read<string>("attemptId"), Read<double>("receivedAfterSeconds")); break;
            case "result": world.ReceiveResult(Read<DecisionResult>("result")); break;
            case "acknowledge": world.AcknowledgeEvents(Read<long>("persistedSequence")); break;
            case "trade": world.Trade(Read<TradeRequest>("request")); break;
            case "quickbar": world.SelectQuickbar(Read<string>("operationId"), Read<int>("index")); break;
            case "farm": world.Farm(Read<FarmRequest>("request")); break;
            case "sleep": world.Sleep(Read<SleepRequest>("request"), Read<double>("realSeconds"), Read<bool>("paused")); break;
            case "produce": world.Produce(Read<ProductionRequest>("request")); break;
            default: throw new InvalidDataException("Unsupported recorded command: " + trace.Command);
        }
    }

    private sealed record ReplayPlayerText(string Text);
    private static void ValidatePlayerArrival(JsonElement input, JsonElement arrival)
    {
        ValidatePlayerBinding(input, arrival.GetProperty("dispatch"));
        Compare(JsonSerializer.SerializeToNode(PlayerArrivalSeconds(arrival.GetProperty("dispatch"), arrival.GetProperty("elapsed"))),
            JsonNode.Parse(input.GetProperty("receivedAfterSeconds").GetRawText()), "model.playerArrivalSeconds");
    }
    private static double PlayerArrivalSeconds(JsonElement dispatch, JsonElement elapsed) => 60
        - TownJson.Deserialize<TimeSpan>(dispatch.GetProperty("request").GetProperty("remainingWait").GetRawText()).TotalSeconds
        + TownJson.Deserialize<TimeSpan>(elapsed.GetRawText()).TotalSeconds;
    private static void ValidatePlayerBinding(JsonElement input, JsonElement dispatch)
    {
        var identity = dispatch.GetProperty("request").GetProperty("identity");
        if (identity.GetProperty("kind").GetString() != "player_text") throw new ReplayMismatch("model.kind", "player_text", identity.GetProperty("kind").GetString());
        foreach (string field in new[] { "sessionId", "playerSessionId", "logicalRequestId" })
            Compare(JsonNode.Parse(identity.GetProperty(field).GetRawText()), JsonNode.Parse(input.GetProperty(field).GetRawText()), "model." + field);
        if (input.TryGetProperty("residentId", out var resident))
            Compare(JsonNode.Parse(identity.GetProperty("residentId").GetRawText()), JsonNode.Parse(resident.GetRawText()), "model.residentId");
    }

    private static void Compare(JsonNode? expected, JsonNode? actual, string field)
    {
        if (expected is JsonObject eo && actual is JsonObject ao)
        {
            foreach (var pair in eo) { if (!ao.ContainsKey(pair.Key)) throw new ReplayMismatch(field + "." + pair.Key, pair.Value?.ToJsonString(), "missing"); Compare(pair.Value, ao[pair.Key], field + "." + pair.Key); }
            foreach (var pair in ao) if (!eo.ContainsKey(pair.Key)) throw new ReplayMismatch(field + "." + pair.Key, "missing", pair.Value?.ToJsonString());
            return;
        }
        if (expected is JsonArray ea && actual is JsonArray aa)
        {
            if (ea.Count != aa.Count) throw new ReplayMismatch(field + ".length", ea.Count.ToString(), aa.Count.ToString());
            for (int i = 0; i < ea.Count; i++) Compare(ea[i], aa[i], field + "[" + i + "]");
            return;
        }
        // Only coordinate components use the numeric tolerance; clocks, assets and identities compare exactly.
        if ((field.EndsWith(".x", StringComparison.Ordinal) || field.EndsWith(".y", StringComparison.Ordinal))
            && expected is JsonValue ev && actual is JsonValue av && ev.TryGetValue<double>(out var e) && av.TryGetValue<double>(out var a)
            && double.IsFinite(e) && double.IsFinite(a) && Math.Abs(e - a) <= CoordinateTolerance) return;
        if (!JsonNode.DeepEquals(expected, actual)) throw new ReplayMismatch(field, expected?.ToJsonString() ?? "null", actual?.ToJsonString() ?? "null");
    }

    private sealed class ReplayMismatch(string field, string? expected, string? actual) : Exception(field)
    {
        public string Field { get; } = field;
        public string? Expected { get; } = expected;
        public string? Actual { get; } = actual;
    }
}
