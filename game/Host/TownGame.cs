using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CozyTown.Core.Town;
using CozyTown.Model;
using Godot;
using File = System.IO.File;

namespace CozyTown.Game;

public partial class TownGame : Node2D
{
    private const double StepSeconds = 0.05;
    private LoadedTownContent _loaded = null!;
    private TownWorld _world = null!;
    private TownState _view = null!;
    private TownRunJournal _journal = null!;
    private readonly List<TownRunJournal> _journals = [];
    private readonly Dictionary<string, TownActor> _actors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Vector2> _lastPositions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FrozenModelRequest> _frozen = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<Action> _mainThread = new();
    private readonly ConcurrentDictionary<string, Stopwatch> _admissionClocks = new(StringComparer.Ordinal);
    private ModelDispatchGate _dispatchGate = new();
    private readonly Stopwatch _hostClock = Stopwatch.StartNew();
    private readonly HashSet<string> _activeModelRequests = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pauseReasons = new(StringComparer.Ordinal);
    private readonly List<object> _spaceQueries = [];
    private readonly List<ModelCompletion> _modelCompletions = [];
    private readonly List<TownEvent> _experimentEvents = [];
    private readonly List<TownEvent> _recentEvents = [];
    private readonly List<DeepSeekClient> _clients = [];
    private DeepSeekClient? _model;
    private DeepSeekOptions? _modelOptions;
    private Camera2D _camera = null!;
    private Node2D _map = null!;
    private TownActor _player = null!;
    private string _scenario = "baseline";
    private string _mode = "rules";
    private string _userRoot = "";
    private string _savePath = "";
    private string[] _arguments = [];
    private string? _selectedResident;
    private TownInputHost _inputHost = null!;
    private TownReplayHostState? _restoredHost;
    private TownReplayParent? _replayParent;
    private List<TownPoint>? _playerRoute
    {
        get => _inputHost?.State.PlayerRoute?.ToList();
        set
        {
            if (value is null) _inputHost.CancelRoute("interface");
            else if (value.Count > 0) _inputHost.RouteTo(value[^1]);
        }
    }
    private int _playerRouteCursor
    {
        get => _inputHost?.State.PlayerRouteCursor ?? 0;
        set { if (value != _playerRouteCursor) throw new InvalidOperationException("Route cursor must advance through host input."); }
    }
    private long _persistedWorldSequence;
    private double _accumulator => _inputHost?.State.UnconsumedSeconds ?? 0;
    private double _uiTimer;
    private double _elapsed;
    private double _lastHostSeconds;
    private double _playerWalkingUntil;
    private double _toastSeconds;
    private bool _ready;
    private bool _headless;
    private bool _fatal;
    private bool _automated;
    private bool _finishing;
    private string? _failure;

    public override void _Ready() => _ = InitializeAsync();

    private async Task InitializeAsync()
    {
        _arguments = OS.GetCmdlineUserArgs();
        if (_arguments.Contains("--toolchain-smoke"))
        {
            GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, "res://Main.tscn");
            return;
        }
        try
        {
            _headless = DisplayServer.GetName() == "headless";
            _automated = _arguments.Contains("--slice-smoke") || _arguments.Contains("--economy-smoke") || _arguments.Contains("--farming-smoke") || _arguments.Contains("--farming-capacity-smoke") || _arguments.Contains("--production-smoke") || _arguments.Contains("--production-capacity-smoke") || _arguments.Contains("--real-experiment") || _arguments.Contains("--replay-record-smoke");
            _scenario = Argument("--scenario=") ?? "baseline";
            if (Argument("--dialogue-fixture=") is string dialogueFixture && dialogueFixture is not ("success" or "delayed" or "invalid" or "timeout" or "budget"))
                throw new InvalidDataException("Dialogue fixture must be success, delayed, invalid, timeout or budget.");
            _map = GetNode<Node2D>("Map");
            _camera = GetNode<Camera2D>("Camera");
            _loaded = TownContentLoader.Load(_map, _scenario);
            if (!_loaded.Definitions.Residents.Select(r => r.Id).Order().SequenceEqual(new[] { "eli", "mina", "ren", "sora" }))
                throw new InvalidDataException("This slice requires the Mina, Eli, Ren, and Sora content package.");
            _userRoot = Argument("--data-dir=") ?? ProjectSettings.GlobalizePath("user://town");
            if (!Path.IsPathFullyQualified(_userRoot)) throw new InvalidDataException("Data directory must be an absolute path.");
            Directory.CreateDirectory(_userRoot);
            _savePath = Path.Combine(_userRoot, "save.json");
            if (_arguments.Contains("--validate-content"))
            {
                WriteReport(new { passed = true, test = "content", scenario = _scenario, fingerprint = _loaded.Content.Fingerprint,
                    extractorVersion = "scene-rect-v1", _loaded.InputDigest, _loaded.PresentationDigest,
                    dependencies = _loaded.Dependencies, manifest = _loaded.Manifest, definitions = _loaded.Definitions });
                GetTree().Quit(0);
                return;
            }
            string? modelConfig = Argument("--model-config=");
            if (modelConfig is not null && Argument("--dialogue-fixture=") is not null)
                throw new InvalidDataException("Fixed dialogue fixtures require offline rules mode.");
            if (modelConfig is not null && (_scenario is "economy-fixture" or "farming-capacity-fixture" or "production-capacity-fixture" || _arguments.Contains("--economy-smoke") || _arguments.Contains("--farming-smoke") || _arguments.Contains("--farming-capacity-smoke") || _arguments.Contains("--production-smoke") || _arguments.Contains("--production-capacity-smoke") || _arguments.Contains("--replay-record-smoke")))
                throw new InvalidDataException("Economy verification requires offline rules mode.");
            if (modelConfig != null)
            {
                var local = TownJson.Deserialize<LocalModelSettings>(File.ReadAllText(modelConfig));
                if (!Path.IsPathFullyQualified(local.CredentialFile) || !Path.IsPathFullyQualified(local.LedgerFile))
                    throw new InvalidDataException("Local model configuration requires absolute credential and ledger paths.");
                _modelOptions = new(local.CredentialFile, local.LedgerFile);
                _mode = "real";
            }
            if (_arguments.Contains("--real-experiment") && _modelOptions == null)
                throw new InvalidDataException("Real experiment requires an explicit local model configuration.");
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            _world = new TownWorld(_loaded.Content, _scenario, movementQuery: CheckMotion);
            string retirementRoot = _modelOptions is null ? _userRoot : Path.GetDirectoryName(_modelOptions.LedgerFile)!;
            _dialogueRetirements = new TownPlayerDialogueRegistry(Path.Combine(retirementRoot, "player-dialogue-retirements.json"));
            _view = _world.GetView();
            CreateJournal();
            CreateActors();
            CreateInterface();
            TownSpeechOverlay.Install(this, _actors.Values.Append(_player), _ui);
            _ready = true;
            _lastHostSeconds = _hostClock.Elapsed.TotalSeconds;
            SyncActors();
            RefreshInterface();
            Toast("欢迎来到榆溪。走近邻居，听听他们今天的打算。", 7);
            _recordingSmokeInitialMemory = GC.GetTotalMemory(false);
            if (_arguments.Contains("--slice-smoke")) _ = RunSceneSmokeAsync();
            else if (_arguments.Contains("--economy-smoke")) _ = RunEconomySmokeAsync();
            else if (_arguments.Contains("--farming-smoke")) _ = RunFarmingSmokeAsync();
            else if (_arguments.Contains("--farming-capacity-smoke")) _ = RunFarmingCapacitySmokeAsync();
            else if (_arguments.Contains("--production-smoke")) _ = RunProductionSmokeAsync();
            else if (_arguments.Contains("--production-capacity-smoke")) _ = RunProductionCapacitySmokeAsync();
        }
        catch (Exception error)
        {
            Fail("startup_failed", error);
            if (_automated || _arguments.Contains("--validate-content"))
            {
                WriteReport(new { passed = false, test = "startup", code = _failure });
                GetTree().Quit(1);
            }
        }
    }

    private void CreateJournal()
    {
        TownState full = _world.State;
        _journal = new TownRunJournal(Path.Combine(_userRoot, "runs"), full.BranchId, new
        {
            schemaVersion = 2, branchId = full.BranchId, full.ParentBranchId, full.SourceSnapshotId, full.InitialStateId,
            decisionMode = _mode, stage = _scenario, budgetId = full.BudgetId,
            contentFingerprint = _loaded.Content.Fingerprint, inputDigest = _loaded.InputDigest,
            presentationDigest = _loaded.PresentationDigest, dependencies = _loaded.Dependencies,
            modelProtocol = TownModelProtocol.Version, rulesVersion = TownWorld.RulesVersion,
            numericVersion = TownWorld.NumericVersion, engineVersion = Engine.GetVersionInfo()["string"].AsString(),
            buildVersion = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            worldMinutesPerSecond = .5, fixedStepSeconds = StepSeconds, bodyRadius = TownContent.BodyRadius,
            recordScope = "Versioned core and host inputs, ordered spatial facts and complete checkpoints; offline replay v1."
        });
        _inputHost = new TownInputHost(_loaded.Content, _world, _restoredHost ?? new(0, null, 0,
            _pauseReasons.Order(StringComparer.Ordinal).ToArray(), new Dictionary<string, string>()));
        _restoredHost = null;
        _journal.StartReplay(_loaded.Content, _world, _inputHost, _replayParent);
        _replayParent = null;
        _journals.Add(_journal);
        _journal.WriteArtifact("content.json", new { _loaded.Manifest, _loaded.Definitions });
        _journal.WriteArtifact("initial-state.json", CaptureHostSnapshot());
        string session = _world.SessionId;
        _journal.BeforeSendOnHost = dispatch =>
        {
            string key = session + ":" + dispatch.Request.Identity.LogicalRequestId;
            TimeSpan elapsed = _admissionClocks.TryGetValue(key, out var clock) ? clock.Elapsed : dispatch.Request.RemainingWait;
            string? playerSession = dispatch.Request.Identity.Kind == "player_text" ? dispatch.Request.Identity.PlayerSessionId : null;
            return _dispatchGate.EnqueueAsync(session,
                () => playerSession is null ? _world.MarkDispatched(dispatch.Request.Identity.LogicalRequestId, dispatch.AttemptId)
                    : DialogueHost.Acknowledge(playerSession, dispatch.Request.Identity.LogicalRequestId, dispatch.AttemptId),
                dispatch.Request.RemainingWait - elapsed, playerDialogueId: playerSession);
        };
        _persistedWorldSequence = 0;
        if (_mode == "real" && _modelOptions != null)
        {
            _model = new DeepSeekClient(_modelOptions, _journal);
            _model.ResponseArrived += (dispatch, elapsed) => _mainThread.Enqueue(() =>
            {
                string? playerSession = dispatch.Request.Identity.Kind == "player_text" ? dispatch.Request.Identity.PlayerSessionId : null;
                double arrivedAfter = 60 - dispatch.Request.RemainingWait.TotalSeconds + elapsed.TotalSeconds;
                if (playerSession is null) _world.MarkResponseArrived(dispatch.Request.Identity.SessionId, dispatch.Request.Identity.LogicalRequestId, dispatch.AttemptId, arrivedAfter);
                else _world.MarkPlayerDialogueResponseArrived(dispatch.Request.Identity.SessionId, playerSession, dispatch.Request.Identity.LogicalRequestId, dispatch.AttemptId, arrivedAfter);
            });
            _clients.Add(_model);
            _journal.SetReplayBudget(_model.Budget);
            _journal.WriteArtifact("budget-at-start.json", _model.Budget);
        }
        else _model = null;
        FlushWorldEvents();
    }

    private void CreateActors()
    {
        using var catalog = JsonDocument.Parse(TownContentLoader.ReadText("res://Art/catalog.json"));
        JsonElement actors = catalog.RootElement.GetProperty("actors");
        Node2D parent = GetNode<Node2D>("Actors");
        foreach (var definition in _loaded.Definitions.Residents)
        {
            var actor = new TownActor();
            parent.AddChild(actor);
            actor.Configure(definition.Id, definition.DisplayName, actors.GetProperty(definition.Id).GetString()!);
            _actors.Add(definition.Id, actor);
        }
        _player = new TownActor();
        parent.AddChild(_player);
        _player.Configure("player", "你", actors.GetProperty("player").GetString()!);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!_ready || _fatal || _finishing) return;
        try
        {
            if (_journal.Failed) throw new IOException("Run journal is interrupted.");
            while (_mainThread.TryDequeue(out var work)) work();
            _dispatchGate.Tick(_world.SessionId, _pauseReasons, _world.GetView().CurrentPlayerDialogueId, _fatal);
            double now = _hostClock.Elapsed.TotalSeconds;
            double elapsed = Math.Max(0, now - _lastHostSeconds);
            _lastHostSeconds = now;
            _elapsed += elapsed;
            // Simulation catches up only a bounded interval; logical deadlines still include the entire host delay.
            double accepted = Math.Min(elapsed, .25);
            if (elapsed > accepted) _world.Advance(0, elapsed - accepted, true);
            _inputHost.AcceptElapsed(elapsed);
            while (_accumulator >= StepSeconds)
            {
                if (_journal.Failed) throw new IOException("Run journal is interrupted.");
                _inputHost.TakeStep();
                bool paused = _pauseReasons.Count > 0;
                if (!paused) AdvancePlayer(StepSeconds);
                _world.Advance(paused ? 0 : StepSeconds * .5, StepSeconds, paused);
                FlushWorldEvents();
                if (!paused) DispatchDecisions();
                ProcessPlayerDialogue();
            }
            _view = _world.GetView();
            SyncActors();
            _uiTimer += delta;
            if (_uiTimer >= .2) { _uiTimer = 0; RefreshInterface(); }
            if (_toastSeconds > 0) { _toastSeconds -= delta; if (_toastSeconds <= 0) _toast.Visible = false; }
            if (_arguments.Contains("--real-experiment")) CheckExperimentCompletion();
            if (_arguments.Contains("--replay-record-smoke") && _elapsed >= ReplayRecordingSeconds()) FinishReplayRecordingSmoke();
        }
        catch (Exception error) { Fail("runtime_failed", error); }
    }

    private void AdvancePlayer(double seconds)
    {
        Vector2 direction = new(
            (MovementKeyHeld(Key.D) || MovementKeyHeld(Key.Right) ? 1 : 0) - (MovementKeyHeld(Key.A) || MovementKeyHeld(Key.Left) ? 1 : 0),
            (MovementKeyHeld(Key.S) || MovementKeyHeld(Key.Down) ? 1 : 0) - (MovementKeyHeld(Key.W) || MovementKeyHeld(Key.Up) ? 1 : 0));
        if (direction.LengthSquared() > 0) _selectedResident = null;
        bool followingRoute = _playerRoute is not null;
        _inputHost.Move(seconds, new(direction.X, direction.Y));
        if (followingRoute && direction.LengthSquared() == 0 && _playerRoute is null) Toast("这里走不过去，换一条路试试。");
    }

    private bool CheckMotion(string id, TownPoint from, TownPoint to)
    {
        using var shape = new CircleShape2D { Radius = (float)TownContent.BodyRadius };
        using var query = new PhysicsShapeQueryParameters2D
        {
            Shape = shape, Transform = new Transform2D(0, Vector(from)), Motion = Vector(to) - Vector(from),
            CollisionMask = 1, CollideWithAreas = false, CollideWithBodies = true, Margin = 0.001f
        };
        var space = GetWorld2D().DirectSpaceState;
        bool startClear = space.IntersectShape(query, 1).Count == 0;
        float[] motion = space.CastMotion(query);
        bool clear = startClear && motion.Length >= 2 && motion[0] >= .9999f;
        _spaceQueries.Add(new { id, from, to, radius = TownContent.BodyRadius, startClear, safeFraction = motion.Length > 0 ? motion[0] : 0, clear });
        return clear;
    }

    private void FlushWorldEvents()
    {
        TownEvent[] events = _world.Events.Where(e => e.Sequence > _persistedWorldSequence).ToArray();
        if (events.Length == 0 && _spaceQueries.Count == 0) return;
        _journal.Append("world_boundary", new { events, spaceQueries = _spaceQueries.ToArray() });
        _spaceQueries.Clear();
        if (events.Length > 0)
        {
            _persistedWorldSequence = events[^1].Sequence;
            _world.AcknowledgeEvents(_persistedWorldSequence);
            var terminal = _world.State.TerminalRequestIds.ToHashSet(StringComparer.Ordinal);
            if (_frozen.Keys.Any(terminal.Contains))
            {
                _inputHost.PruneTerminalRequests();
                foreach (string key in _frozen.Keys.Where(terminal.Contains).ToArray()) _frozen.Remove(key);
            }
            if (_automated) _experimentEvents.AddRange(events);
            foreach (TownEvent entry in events)
            {
                if (entry.Kind is not ("time_input" or "space_query")) _recentEvents.Add(entry);
                if (entry.Kind == "dialogue_spoken" && entry.ResidentId != null && _actors.TryGetValue(entry.ResidentId, out var actor))
                {
                    using var data = JsonDocument.Parse(entry.DataJson);
                    if (data.RootElement.TryGetProperty("text", out var text)) actor.Say(text.GetString() ?? "");
                }
                if (entry.Kind == "resource_transferred") Toast("邻居完成了一次双方同意的交换。");
            }
            if (_recentEvents.Count > 120) _recentEvents.RemoveRange(0, _recentEvents.Count - 120);
        }
    }

    private void DispatchDecisions()
    {
        if (_experimentStopping || (_arguments.Contains("--real-experiment") && _elapsed >= 180)) return;
        foreach (DecisionRequest request in _world.GetDecisionRequests())
        {
            string key = _world.SessionId + ":" + request.LogicalRequestId;
            if (_activeModelRequests.Contains(key)) continue;
            if (_mode != "real" || _model == null)
            {
                string attempt = "rules-" + Guid.NewGuid().ToString("N");
                if (_world.MarkDispatched(request.LogicalRequestId, attempt))
                    _world.ReceiveResult(new(_world.SessionId, request.LogicalRequestId, attempt, request.ResidentId,
                        _world.CreateRuleCandidate(request), "rules", null));
                continue;
            }
            if (_activeModelRequests.Count >= 2) break;
            var identity = new ModelRequestIdentity(_world.GetView().BranchId, _world.SessionId, request.LogicalRequestId, request.ResidentId, request.ActivityRevision);
            string body = _frozen.TryGetValue(request.LogicalRequestId, out var old) ? old.InputJson : TownModelProtocol.BuildRequest(request);
            bool originalRecorded = _model.Budget.Attempts.Any(a => a.LogicalRequestId == request.LogicalRequestId && !a.Recovery);
            var frozen = new FrozenModelRequest(identity, _scenario == "baseline" ? ModelStage.Baseline : ModelStage.Counterfactual,
                body, request.IsRecovery || originalRecorded, TimeSpan.FromSeconds(request.RemainingWaitSeconds));
            _inputHost.FreezeRequest(request.LogicalRequestId, TownJson.Serialize(frozen));
            _frozen[request.LogicalRequestId] = frozen;
            _activeModelRequests.Add(key);
            _ = SendModelAsync(_model, request, frozen, key);
        }
        FlushWorldEvents();
    }

    private async Task SendModelAsync(DeepSeekClient client, DecisionRequest request, FrozenModelRequest frozen, string key)
    {
        var admissionClock = Stopwatch.StartNew();
        _admissionClocks[key] = admissionClock;
        try
        {
            ModelCompletion result = await client.SendAsync(frozen);
            double arrivedAfter = 60 - frozen.RemainingWait.TotalSeconds + (result.LogicalElapsedToArrival ?? admissionClock.Elapsed).TotalSeconds;
            var responseJournal = _journals.Single(j => Path.GetFileName(j.DirectoryPath) == frozen.Identity.RunId);
            responseJournal.RecordReplayInput("model_mailbox_enqueued", new { dispatch = result.Dispatch });
            _mainThread.Enqueue(() =>
            {
                _journal.ImportRetiredCompletion(responseJournal, result);
                _journal.RecordReplayInput("model_mailbox_entered", new { dispatch = result.Dispatch });
                _activeModelRequests.Remove(key);
                _modelCompletions.Add(result);
                ActionCandidate? candidate = null;
                string? error = result.FailureCode;
                if (result.Succeeded && result.CandidateJson != null)
                {
                    try { candidate = TownModelProtocol.Parse(result.CandidateJson); }
                    catch { error = "candidate_schema_invalid"; }
                }
                if (result.DispatchedAtUtc == null && _world.SessionId == frozen.Identity.SessionId)
                    _world.MarkDispatched(request.LogicalRequestId, result.Dispatch.AttemptId);
                _world.ReceiveResult(new(frozen.Identity.SessionId, request.LogicalRequestId, result.Dispatch.AttemptId,
                    request.ResidentId, candidate, "real_model", error,
                    arrivedAfter));
                if (error != null) Toast("模型回复未采用，居民会按规则处理。详情见观察记录。");
            });
        }
        catch (Exception error)
        {
            string code = error is ModelException modelError ? modelError.Code : "model_unavailable";
            _mainThread.Enqueue(() =>
            {
                _activeModelRequests.Remove(key);
                if (_world.SessionId != frozen.Identity.SessionId) return;
                _journal.Append("dispatch_rejected", new { request.LogicalRequestId, request.ResidentId, code });
                if (code is "concurrency_limit" or "budget_concurrency_limit") return;
                string attempt = "not-sent-" + Guid.NewGuid().ToString("N");
                if (_world.MarkDispatched(request.LogicalRequestId, attempt))
                    _world.ReceiveResult(new(_world.SessionId, request.LogicalRequestId, attempt, request.ResidentId, null, "rule_fallback", code));
                Toast("真实调用暂不可用，已明确转为规则回退。");
            });
        }
        finally { _admissionClocks.TryRemove(key, out _); }
    }

    private void SyncActors()
    {
        if (_map is TownBackdrop backdrop) backdrop.UpdateFarming(_loaded.Definitions.Farming!, _view.Farming);
        if (_map is TownBackdrop productionBackdrop) productionBackdrop.UpdateProduction(_loaded.Definitions.Production!, _view.Production);
        foreach (ResidentState resident in _view.Residents)
        {
            var actor = _actors[resident.Id];
            Vector2 position = Vector(resident.Position);
            bool walking = _pauseReasons.Count == 0 && resident.RouteCursor < resident.Route.Count && resident.BlockedReason == null;
            actor.SetPose(position, Vector(resident.Facing), walking);
            actor.Highlighted = _selectedResident == resident.Id;
            _lastPositions[resident.Id] = position;
        }
        Vector2 player = Vector(_view.PlayerPosition);
        if (_lastPositions.TryGetValue("player", out Vector2 last) && last.DistanceTo(player) > .01) _playerWalkingUntil = _elapsed + .09;
        bool playerWalking = _pauseReasons.Count == 0 && _elapsed < _playerWalkingUntil;
        _player.SetPose(player, Vector(_view.PlayerFacing), playerWalking);
        _lastPositions["player"] = player;
        _camera.Position = _selectedResident == null ? player : Vector(_view.Residents.Single(r => r.Id == _selectedResident).Position);
        _camera.Offset = _inspector.Visible ? new Vector2(70, 0) : Vector2.Zero;
    }

    private static bool MovementKeyHeld(Key key) => Input.IsPhysicalKeyPressed(key) || Input.IsKeyPressed(key);

    public override void _Input(InputEvent input)
    {
        if (!_ready || input is not InputEventKey { Pressed: true, Echo: false } key) return;
        // Windows virtual input can report a scan code for a different key; shortcuts use logical identity.
        Key pressed = key.Keycode != Key.None ? key.Keycode : key.PhysicalKeycode;
        if (_fatal)
        {
            if (pressed == Key.F9) LoadWorld();
            else if (pressed == Key.Escape) _menu.Visible = !_menu.Visible;
            return;
        }
        if (pressed == Key.Escape) ToggleMenu();
        else if (pressed == Key.E && _pauseReasons.Count == 0) InteractNearby();
        else if (pressed == Key.B) ToggleInventory();
        else if (pressed >= Key.Key1 && pressed <= Key.Key5 && GetViewport().GuiGetFocusOwner() is not LineEdit)
            SelectQuickbarSlot((int)pressed - (int)Key.Key1);
        else if (pressed == Key.Tab) ToggleInspector();
        else if (pressed == Key.F5) SaveWorld();
        else if (pressed == Key.F9) LoadWorld();
        else return;
        GetViewport().SetInputAsHandled();
    }

    public override void _UnhandledInput(InputEvent input)
    {
        if (!_ready || _fatal) return;
        if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } && _pauseReasons.Count == 0)
        {
            TownPoint target = Point(GetGlobalMousePosition());
            ResidentState? nearby = _view.Residents.OrderBy(r => r.Position.DistanceTo(target)).FirstOrDefault();
            if (nearby != null && nearby.Position.DistanceTo(target) < 17) { SelectResident(nearby.Id); return; }
            _selectedResident = null;
            _inputHost.RouteTo(target);
            _journal.Append("player_route_requested", new { target, route = _playerRoute });
            if (_playerRoute == null) Toast("这里暂时走不到。");
        }
    }

    public override void _Notification(int what)
    {
        if (!_ready || _headless || _automated) return;
        if (what == NotificationApplicationFocusOut) SetPause("focus", true);
        else if (what == NotificationApplicationFocusIn) SetPause("focus", false);
    }

    private void SetPause(string reason, bool pause)
    {
        if (pause) _pauseReasons.Add(reason); else _pauseReasons.Remove(reason);
        if (_inputHost is not null) _inputHost.Pause(reason, pause);
        if (_journal != null && !_journal.Failed) _journal.Append("pause_changed", new { reason, pause, activeReasons = _pauseReasons.Order().ToArray() });
        if (_ready) RefreshInterface();
    }

    private TownHostSnapshot CaptureHostSnapshot() => new(1, _world.CaptureJson(), Path.GetFileName(_journal?.DirectoryPath ?? _world.GetView().BranchId),
        _journal?.RecordCount ?? 0, _accumulator, _mode, _scenario, new Dictionary<string, FrozenModelRequest>(_frozen),
        _playerRoute?.ToArray(), _playerRoute == null ? 0 : _playerRouteCursor);

    private void SaveWorld()
    {
        if (_fatal) return;
        bool published = false;
        try
        {
            while (_mainThread.TryDequeue(out var work)) work();
            FlushWorldEvents();
            if (_world.HasUncommittedResponses) { Toast("回复正在落盘，请稍后保存。原存档保持不变。"); return; }
            var snapshot = CaptureHostSnapshot();
            var worldSnapshot = TownJson.Deserialize<TownSnapshot>(snapshot.WorldJson);
            _journal.WriteArtifact("prefix-" + worldSnapshot.SnapshotId + ".json", new TownReplayParent(_journal.CaptureReplay(), worldSnapshot, _inputHost.State));
            TownSaveFile.Write(_savePath, snapshot, candidate => _ = TownWorld.RestoreCandidate(_loaded.Content, candidate.WorldJson));
            published = true;
            _journal.Append("snapshot_saved", new { snapshot.JournalRecordCount, worldSequence = _view.Sequence });
            Toast("已保存这一刻。下次会从原来的行程继续。");
        }
        catch (Exception error)
        {
            if (_journal.Failed) Fail("journal_failed", error);
            else
            {
                try { _journal.Append("save_failed", new { code = error.GetType().Name }); }
                catch (Exception journalError) { Fail("journal_failed", journalError); }
            }
            Toast(published ? "存档已写入，但后续记录失败，小镇已暂停。" : "保存失败，之前的存档仍被保留。", _fatal ? 9999 : 4);
        }
    }

    private void LoadWorld()
    {
        if (!File.Exists(_savePath)) { Toast("还没有存档，可以先保存当前进度。"); return; }
        bool committed = false;
        bool recoveringFault = _fatal;
        try
        {
            SetPause("loading", true);
            if (!recoveringFault) while (_mainThread.TryDequeue(out var work)) work();
            else _spaceQueries.Clear();
            var snapshot = TownSaveFile.Read(_savePath);
            if (snapshot.Stage != _scenario) throw new InvalidDataException("Saved initial state differs from the current content.");
            if (snapshot.DecisionMode == "real" && _modelOptions == null)
                throw new InvalidDataException("Real-mode restore requires explicit model configuration.");
            TownWorld candidate = TownWorld.RestoreCandidate(_loaded.Content, snapshot.WorldJson, CheckMotion);
            var restoredDialogue = candidate.GetView().PlayerDialogues.SingleOrDefault(d => d.Id == candidate.GetView().CurrentPlayerDialogueId);
            string? retirement = restoredDialogue is { Request: not null, TerminalReason: null }
                ? _dialogueRetirements?.Find(candidate.GetView().BudgetId, restoredDialogue.Request.LogicalRequestId) : null;
            if (snapshot.PlayerRoute != null)
            {
                TownPoint previous = candidate.GetView().PlayerPosition;
                foreach (TownPoint point in snapshot.PlayerRoute.Skip(snapshot.PlayerRouteCursor))
                {
                    if (!_loaded.Content.CanMove(previous, point)) throw new InvalidDataException("Saved player route is not walkable.");
                    previous = point;
                }
            }
            foreach (ResidentState resident in candidate.GetView().Residents)
                if (!CheckMotion(resident.Id, resident.Position, resident.Position)) throw new InvalidDataException("Saved body overlaps scene geometry.");
            if (!CheckMotion("player", candidate.GetView().PlayerPosition, candidate.GetView().PlayerPosition))
                throw new InvalidDataException("Saved player overlaps scene geometry.");
            if (!_journal.Failed) FlushWorldEvents();
            string prefixPath = Path.Combine(_userRoot, "runs", snapshot.SourceRunDirectoryName,
                "prefix-" + candidate.State.SourceSnapshotId + ".json");
            _replayParent = File.Exists(prefixPath) ? TownJson.Deserialize<TownReplayParent>(File.ReadAllText(prefixPath)) : null;
            _world = candidate;
            committed = true;
            _recentEvents.Clear();
            _frozen.Clear();
            foreach (var pair in snapshot.ModelRequests) _frozen.Add(pair.Key, pair.Value);
            _mode = snapshot.DecisionMode;
            _restoredHost = new(snapshot.UnconsumedSeconds, snapshot.PlayerRoute, snapshot.PlayerRouteCursor,
                _pauseReasons.Order(StringComparer.Ordinal).ToArray(), snapshot.ModelRequests.ToDictionary(pair => pair.Key, pair => TownJson.Serialize(pair.Value)));
            _view = _world.GetView();
            CreateJournal();
            if (retirement is not null) _world.ApplyPlayerDialogueRetirement(restoredDialogue!.Id, retirement);
            _view = _world.GetView();
            SetPause("dialogue", _view.CurrentPlayerDialogueId is not null);
            RefreshPlayerDialogue();
            _lastHostSeconds = _hostClock.Elapsed.TotalSeconds;
            if (recoveringFault)
            {
                _dispatchGate = new ModelDispatchGate();
                _fatal = false;
                _failure = null;
                SetPause("fault", false);
            }
            SyncActors();
            string sourceRecords = Path.Combine(_userRoot, "runs", snapshot.SourceRunDirectoryName, "events.jsonl");
            bool sourcePrefixAvailable = HasJournalPrefix(sourceRecords, snapshot.JournalRecordCount);
            _journal.Append("restore_committed", new { snapshot.SourceRunDirectoryName, snapshot.JournalRecordCount, _view.SourceSnapshotId,
                recoveringFault, sourcePrefixAvailable, recordStatus = sourcePrefixAvailable ? "source_prefix_present" : "source_prefix_missing_replay_unavailable" });
            Toast("已续接原来的路线、约定与交谈。");
        }
        catch (Exception error)
        {
            if (committed) { Fail("restore_publication_failed", error); return; }
            Toast("无法读取这份存档，当前小镇保持原样。");
            if (_journal.Failed) Fail("journal_failed", error);
            else _journal.Append("restore_rejected", new { code = error.GetType().Name });
        }
        finally { if (!_fatal) SetPause("loading", false); }
    }

    private void ExportRun()
    {
        try
        {
            FlushWorldEvents();
            _journal.WriteArtifact("export-state.json", CaptureHostSnapshot());
            if (_model != null) _journal.WriteArtifact("budget-at-export.json", _model.Budget);
            string path = Path.Combine(_userRoot, "exports", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] + ".zip");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _journal.ExportSnapshotZip(path);
            Toast("本次观察记录已导出。保存目录已打开。", 5);
            if (!_automated) OS.ShellOpen(Path.GetDirectoryName(path)!);
        }
        catch { Toast("导出失败，已有运行记录仍保留。"); }
    }

    private void Fail(string code, Exception error)
    {
        _fatal = true;
        _failure = code + ":" + error.GetType().Name;
        _pauseReasons.Add("fault");
        _dispatchGate.Stop();
        GD.PushError(_failure);
        if (_mode == "rules") GD.PrintErr(error.ToString());
        if (_journal is not null && !_journal.Failed)
        {
            try { _journal.WriteArtifact("replay.json", _journal.CaptureReplay()); }
            catch { }
        }
        if (!_ready && !_headless)
        {
            var layer = new CanvasLayer();
            AddChild(layer);
            var message = new Godot.Label { Text = "小镇无法启动\n" + _failure + "\n请检查内容验证记录后重新启动。", Position = new Vector2(40, 40) };
            layer.AddChild(message);
        }
        if (_ready) Toast("小镇已暂停：" + _failure + "。当前状态不会继续改变。", 9999);
        if (_ready && _automated) WriteFailureReport();
    }

    public override void _ExitTree() => _dispatchGate.Stop();

    private static bool HasJournalPrefix(string path, long count)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            for (long sequence = 1; sequence <= count; sequence++)
            {
                string? line = reader.ReadLine();
                if (line == null) return false;
                using var entry = JsonDocument.Parse(line);
                if (entry.RootElement.GetProperty("sequence").GetInt64() != sequence) return false;
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException) { return false; }
    }

    private string? Argument(string prefix) => _arguments.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
    private static Vector2 Vector(TownPoint point) => new((float)point.X, (float)point.Y);
    private static TownPoint Point(Vector2 point) => new(point.X, point.Y);
    private void WriteReport(object report)
    {
        string? path = Argument("--report=");
        if (path == null || !Path.IsPathFullyQualified(path)) throw new InvalidDataException("Verification requires --report=<absolute path>.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(report, TownJson.Options));
    }

    private sealed record LocalModelSettings(string CredentialFile, string LedgerFile);
}
