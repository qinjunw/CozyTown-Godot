using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CozyTown.Core.Town;
using CozyTown.Model;
using Godot;
using File = System.IO.File;

namespace CozyTown.Game;

public partial class TownGame
{
    private TownFullDemoPlan? _fullDemoPlan;
    private string? _fullDemoPlanHash;
    private bool _fullDemoFixed;
    private TownFullDemoWindow? _fullDemoWindow;
    private readonly HashSet<string> _fullDemoAdmitted = new(StringComparer.Ordinal);
    private readonly List<object> _fullDemoCommands = [];
    private readonly List<object> _fullDemoWindows = [];
    private int _fullDemoCursor;
    private int _fullDemoFixedSteps;
    private bool _fullDemoSaveCompleted;
    private double? _fullDemoWindowStoppedAt;
    private string? _fullDemoDispatchStopReason;

    private void StopFullDemoAdmission(string? code, bool ledgerStopped = false)
    {
        if (_fullDemoPlan is null || code is null || !ledgerStopped && !TownFullDemoWindow.StopsDispatch(code)) return;
        _fullDemoDispatchStopReason ??= code;
        _fullDemoWindow?.RejectAdmission(code, ledgerStopped);
        NoticeFullDemoWindowStop();
        _journal.Append("full_demo_dispatch_stopped", new { reason = code, day = _fullDemoWindow?.Day,
            cursor = _fullDemoCursor, acceptedRealSeconds = _elapsed });
    }

    private void NoticeFullDemoWindowStop()
    {
        if (_fullDemoWindow?.StopReason is not null) _fullDemoWindowStoppedAt ??= _elapsed;
    }

    private void ConfigureFullDemoInput()
    {
        string? mode = Argument("--full-demo-evaluation=");
        if (mode is null) return;
        if (mode is not ("fixed" or "real")) throw new InvalidDataException("Full-demo evaluation mode must be fixed or real.");
        if (_scenario is not ("baseline" or "counterfactual")) throw new InvalidDataException("Full-demo evaluation requires baseline or counterfactual initial state.");
        if (_arguments.Any(a => a.EndsWith("-smoke", StringComparison.Ordinal) || a == "--real-experiment")
            || Argument("--dialogue-fixture=") is not null)
            throw new InvalidDataException("Full-demo evaluation cannot be combined with another verification driver.");
        _fullDemoFixed = mode == "fixed";
        string? path = Argument("--full-demo-input=");
        if (path is not null && !Path.IsPathFullyQualified(path)) throw new InvalidDataException("Full-demo input requires an absolute path.");
        string json = path is null ? TownContentLoader.ReadText("res://Content/full-demo-input.json") : File.ReadAllText(path);
        _fullDemoPlan = TownFullDemoPlan.Read(json);
        _fullDemoPlanHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(TownJson.Serialize(_fullDemoPlan))));
    }

    private void ValidateFullDemoMode()
    {
        if (_fullDemoPlan is null)
        {
            if (_arguments.Contains("--real-experiment") && _modelRuntime.Profile.Version != ModelBudgetProfile.FirstSliceVersion)
                throw new InvalidDataException("First-slice experiment requires its original budget profile.");
            return;
        }
        if (!_modelRuntime.IsFullDemo || _modelRuntime.ExecutionPurpose != "experiment"
            || _fullDemoFixed && _modelOptions is not null || !_fullDemoFixed && _modelOptions is null)
            throw new InvalidDataException("Full-demo fixed mode requires an isolated profile; real mode requires explicit full-demo experiment configuration.");
        if (Argument("--report=") is not string report || !Path.IsPathFullyQualified(report))
            throw new InvalidDataException("Full-demo evaluation requires --report=<absolute path>.");
    }

    private bool AdmitFullDemoNpc(DecisionRequest request)
    {
        if (_fullDemoDispatchStopReason is not null) return false;
        if (_fullDemoAdmitted.Contains(request.LogicalRequestId)) return true;
        if (_fullDemoWindow is null) return false;
        if (!_fullDemoWindow.TryAdmit(request.IsRecovery)) return false;
        _fullDemoAdmitted.Add(request.LogicalRequestId);
        _journal.Append("full_demo_npc_admitted", new { day = _fullDemoWindow.Day, request.LogicalRequestId,
            request.ResidentId, request.IsRecovery, ordinaryAdmissions = _fullDemoWindow.OrdinaryAdmissions,
            elapsedSeconds = _fullDemoWindow.ElapsedSeconds, source = _fullDemoFixed ? "fixed" : "real" });
        return true;
    }

    private async Task FullDemoTickAsync()
    {
        if (_fatal) throw new InvalidDataException("Full-demo host stopped: " + _failure);
        if (_fullDemoFixed)
        {
            AdvanceHostElapsed(_fullDemoPlan!.StepSeconds);
            if (++_fullDemoFixedSteps % 100 == 0) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        else await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    }

    private async Task FullDemoWalkAsync(Func<TownPoint> target)
    {
        double start = _elapsed, nextRoute = start;
        while (_world.State.PlayerPosition.DistanceTo(target()) > 12)
        {
            if (_elapsed - start >= _fullDemoPlan!.WalkTimeoutSeconds) throw new InvalidDataException("Required full-demo route did not arrive.");
            if (_elapsed >= nextRoute)
            {
                _inputHost.RouteTo(target());
                if (_inputHost.State.PlayerRoute is null) throw new InvalidDataException("Required full-demo route is unavailable.");
                nextRoute = _elapsed + 1;
            }
            await FullDemoTickAsync();
        }
        _inputHost.CancelRoute("full_demo_arrived");
        FlushWorldEvents();
    }

    private async Task FullDemoSaveLoadAsync()
    {
        string sourceBranch = _world.State.BranchId;
        double start = _elapsed;
        while (true)
        {
            SaveWorld();
            if (File.Exists(_savePath) && TownSaveFile.Read(_savePath).SourceRunDirectoryName == sourceBranch) break;
            if (_elapsed - Math.Min(start, _fullDemoWindowStoppedAt ?? start) >= _fullDemoPlan!.DrainSeconds)
                throw new InvalidDataException("Required full-demo save was not published within the remaining drain time.");
            await FullDemoTickAsync();
        }
        int cursor = _fullDemoCursor, admissions = _fullDemoWindow!.OrdinaryAdmissions;
        double elapsed = _fullDemoWindow.ElapsedSeconds;
        double priorHost = _lastHostSeconds;
        LoadWorld();
        if (!_fullDemoFixed)
        {
            double now = _hostClock.Elapsed.TotalSeconds;
            AdvanceHostElapsed(Math.Max(0, now - priorHost));
            _lastHostSeconds = now;
        }
        if (_world.State.ParentBranchId != sourceBranch || _world.State.BranchId == sourceBranch
            || _fullDemoCursor != cursor || _fullDemoWindow.OrdinaryAdmissions < admissions || _fullDemoWindow.ElapsedSeconds < elapsed)
            throw new InvalidDataException("Full-demo restore did not preserve the driver cursor and window limits.");
        _fullDemoSaveCompleted = true;
        _journal.Append("full_demo_save_load_completed", new { sourceBranch, cursor, admissions, elapsed,
            branchId = _world.State.BranchId, pendingRequests = _world.State.PendingDecisions.Count });
    }

    private async Task FullDemoWindowAsync(int day)
    {
        _fullDemoWindow = new(_fullDemoPlan!, day);
        _fullDemoWindowStoppedAt = null;
        double startMinute = _world.WorldMinute;
        int eventStart = _experimentEvents.Count;
        _journal.Append("full_demo_window_started", new { day, worldMinute = startMinute, inputSha256 = _fullDemoPlanHash });
        while (_fullDemoWindow.StopReason is null)
        {
            if (_model is not null)
            {
                var budget = _model.Budget;
                if (budget.StopReason is not null)
                {
                    _fullDemoDispatchStopReason ??= "budget_stopped:" + budget.StopReason;
                    _fullDemoWindow.Stop(_fullDemoDispatchStopReason);
                }
                else if (budget.SlotsHeld >= 2 && budget.Attempts.Count(a => a.SlotHeld && a.Status == "unknown") >= 2)
                {
                    _fullDemoDispatchStopReason ??= "unknown_slots";
                    _fullDemoWindow.Stop(_fullDemoDispatchStopReason);
                }
            }
            NoticeFullDemoWindowStop();
            if (day == 2 && !_fullDemoSaveCompleted && _fullDemoWindow.SaveDue && _fullDemoWindow.StopReason is null)
                await FullDemoSaveLoadAsync();
            if (_fullDemoWindow.StopReason is null) await FullDemoTickAsync();
        }
        NoticeFullDemoWindowStop();
        double drainStart = _fullDemoWindowStoppedAt!.Value;
        if (day == 2 && !_fullDemoSaveCompleted) await FullDemoSaveLoadAsync();
        do { await FullDemoTickAsync(); }
        while (_elapsed - drainStart < _fullDemoPlan!.DrainSeconds
            && (_activeModelRequests.Count > 0 || _world.HasUncommittedResponses
                || _world.State.PendingDecisions.Any(p => _fullDemoAdmitted.Contains(p.Request.LogicalRequestId))));
        bool drained = _activeModelRequests.Count == 0 && !_world.HasUncommittedResponses
            && !_world.State.PendingDecisions.Any(p => _fullDemoAdmitted.Contains(p.Request.LogicalRequestId));
        var coverage = _loaded.Definitions.Residents.Select(r => new { residentId = r.Id,
            adopted = _experimentEvents.Skip(eventStart).Count(e => e.Kind == "candidate_executed" && e.ResidentId == r.Id
                && e.Source == (_fullDemoFixed ? "rules" : "real_model")),
            ruleFallbacks = _experimentEvents.Skip(eventStart).Count(e => e.Kind == "rule_fallback" && e.ResidentId == r.Id) }).ToArray();
        var result = new { day, startMinute, endMinute = _world.WorldMinute, elapsedSeconds = _fullDemoWindow.ElapsedSeconds,
            ordinaryAdmissions = _fullDemoWindow.OrdinaryAdmissions, _fullDemoWindow.StopReason,
            dispatchStopReason = _fullDemoDispatchStopReason,
            drainSeconds = _elapsed - drainStart, drained, coverage, requiredSaveCompleted = day != 2 || _fullDemoSaveCompleted };
        _fullDemoWindows.Add(result);
        _journal.Append("full_demo_window_finished", result);
        _fullDemoWindow = null;
        if (!drained || coverage.Any(c => c.adopted == 0) || _fullDemoDispatchStopReason is not null)
            throw new InvalidDataException("Full-demo window has incomplete coverage, pending admitted requests or a dispatch stop.");
    }

    private async Task FullDemoDialogueAsync(string residentId)
    {
        if (_fullDemoDispatchStopReason is not null) throw new InvalidDataException("Full-demo dispatch remains stopped: " + _fullDemoDispatchStopReason);
        await FullDemoWalkAsync(() => _world.GetView().Residents.Single(r => r.Id == residentId).Position);
        TalkResident(residentId);
        if (DialogueHost.Current?.ResidentId != residentId) throw new InvalidDataException("Required player conversation did not open.");
        GeneratePlayerDialogue();
        double start = _elapsed;
        while (DialogueHost.Current is { TerminalReason: null })
        {
            if (_elapsed - start > 61) throw new InvalidDataException("Player conversation exceeded its logical wait.");
            await FullDemoTickAsync();
        }
        var dialogue = DialogueHost.Current!;
        bool displayed = dialogue.Displays.Any(d => d.Source == (_fullDemoFixed ? "fixed_model" : "real_model") && d.Error is null);
        _journal.Append("full_demo_player_sample", new { residentId, dialogue.Id, displayed, source = _fullDemoFixed ? "fixed" : "real" });
        CloseDialogue();
        await FullDemoTickAsync();
        if (!displayed) throw new InvalidDataException("Required player text was not displayed from the selected client.");
    }

    private async Task RunFullDemoEvaluationAsync()
    {
        var watch = Stopwatch.StartNew();
        try
        {
            foreach (var command in _fullDemoPlan!.Commands)
            {
                double before = _elapsed, beforeMinute = _world.WorldMinute;
                _journal.Append("full_demo_command_started", new { cursor = _fullDemoCursor, command, before, beforeMinute });
                switch (command.Kind)
                {
                    case "walk": await FullDemoWalkAsync(() => _loaded.Content.Place(command.Target!).Position); break;
                    case "buy":
                        OpenEconomy(command.Target); _tradeResourceId = command.Resource; _tradeQuantity.Value = command.Quantity;
                        int held = _world.State.Economy.Player.Inventory.GetValueOrDefault(command.Resource!);
                        SubmitTrade(command.Quantity); CloseEconomy();
                        if (_world.State.Economy.Player.Inventory.GetValueOrDefault(command.Resource!) != held + command.Quantity)
                            throw new InvalidDataException("Required full-demo purchase failed.");
                        break;
                    case "plant": case "water":
                        OpenFarming(command.Target); SubmitFarm(command.Kind, command.Resource); CloseFarming();
                        var plot = _world.State.Farming.Plots.Single(p => p.Id == command.Target);
                        if (command.Kind == "plant" && plot.CropId != command.Resource || command.Kind == "water" && !plot.Watered)
                            throw new InvalidDataException("Required full-demo crop command failed.");
                        break;
                    case "feed": case "collect":
                        OpenProduction(command.Target!); SubmitProduction(command.Kind); CloseProduction();
                        var hen = _world.State.Production.Chickens.Single(h => h.Id == command.Target);
                        if (command.Kind == "feed" && !hen.Fed || command.Kind == "collect" && hen.ProductReady)
                            throw new InvalidDataException("Required full-demo chicken command failed.");
                        break;
                    case "sleep":
                        OpenFarming(null); _sleepHours.Value = command.Quantity; SubmitSleep(command.Quantity); CloseFarming();
                        if (Math.Abs(_world.WorldMinute - beforeMinute - command.Quantity * 60) > .00001)
                            throw new InvalidDataException("Required full-demo sleep failed.");
                        break;
                    case "window": await FullDemoWindowAsync(command.Quantity); break;
                    case "player_dialogue": await FullDemoDialogueAsync(command.Target!); break;
                    case "finish": break;
                    default: throw new InvalidDataException("Unsupported full-demo input command: " + command.Kind);
                }
                if (_fatal) throw new InvalidDataException("Full-demo business input failed.");
                var completed = new { cursor = _fullDemoCursor++, command, before, beforeMinute,
                    elapsedSeconds = _elapsed, worldMinute = _world.WorldMinute, branchId = _world.State.BranchId };
                _fullDemoCommands.Add(completed);
                _journal.Append("full_demo_command_completed", completed);
            }
            FlushWorldEvents();
            if (_world.State.Farming.Mornings.Count != 1 || _world.State.Farming.SleepReceipts.Count != 2
                || !_fullDemoSaveCompleted || _world.State.PlayerDialogues.Count(d => d.Displays.Any(t => t.Source == (_fullDemoFixed ? "fixed_model" : "real_model") && t.Error is null)) != 4)
                throw new InvalidDataException("Full-demo final state is missing a required morning, sleep, restore or player text.");
            _finishing = true;
            _journal.WriteArtifact("full-demo-final-state.json", CaptureHostSnapshot());
            if (_model is not null) _journal.WriteArtifact("full-demo-final-budget.json", _model.Budget);
            var branches = _journals.Select(ExportFullDemoBranch).ToArray();
            var report = new { schemaVersion = 1, test = "full_demo_evaluation", passed = true, mode = _fullDemoFixed ? "fixed" : "real",
                realAcceptanceEvaluated = false,
                scenario = _scenario, input = _fullDemoPlan, inputSha256 = _fullDemoPlanHash,
                budgetProfileVersion = _modelRuntime.Profile.Version, budgetId = _world.State.BudgetId,
                executionPurpose = _modelRuntime.ExecutionPurpose, acceptedRealSeconds = _elapsed, wallSeconds = watch.Elapsed.TotalSeconds,
                windows = _fullDemoWindows, commands = _fullDemoCommands, branches,
                mornings = _world.State.Farming.Mornings.Count, sleeps = _world.State.Farming.SleepReceipts.Count,
                finalState = CaptureHostSnapshot(), events = _experimentEvents,
                budget = _model?.Budget, completions = _modelCompletions,
                realHttpSends = _modelCompletions.Count(c => c.DispatchedAtUtc is not null),
                peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
                toolchain = new { engine = Engine.GetVersionInfo()["string"].AsString(), scene = SceneDependencies() } };
            WriteReport(report);
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            _experimentStopping = true;
            _finishing = true;
            BudgetSnapshot? budget = null;
            string? budgetReadFailure = null, flushFailure = null;
            try { budget = _model?.Budget; }
            catch (Exception fault) { budgetReadFailure = fault.GetType().Name; }
            try { FlushWorldEvents(); }
            catch (Exception fault) { flushFailure = fault.GetType().Name; }
            var branches = new List<object>();
            var exportFailures = new List<object>();
            foreach (var journal in _journals)
            {
                try { branches.Add(ExportFullDemoBranch(journal)); }
                catch (Exception fault) { exportFailures.Add(new { journal.DirectoryPath, code = fault.GetType().Name }); }
            }
            try
            {
                WriteReport(new { schemaVersion = 1, test = "full_demo_evaluation", passed = false,
                    mode = _fullDemoFixed ? "fixed" : "real", scenario = _scenario, cursor = _fullDemoCursor,
                    error = error.Message, acceptedRealSeconds = _elapsed, wallSeconds = watch.Elapsed.TotalSeconds,
                    inputSha256 = _fullDemoPlanHash, dispatchStopReason = _fullDemoDispatchStopReason,
                    windows = _fullDemoWindows, commands = _fullDemoCommands, branches, exportFailures, flushFailure,
                    events = _experimentEvents, budget, budgetReadFailure, completions = _modelCompletions,
                    runDirectories = _journals.Select(j => j.DirectoryPath) });
            }
            catch (Exception fault) { GD.PushError("Full-demo failure report could not be written: " + fault.GetType().Name); }
            finally { GetTree().Quit(1); }
        }
    }

    private object ExportFullDemoBranch(TownRunJournal journal)
    {
        var package = journal.CaptureReplay();
        journal.WriteArtifact("replay.json", package);
        string export = Path.Combine(_userRoot, "exports", Path.GetFileName(journal.DirectoryPath) + ".zip");
        journal.ExportSnapshotZip(export);
        return new { branchId = Path.GetFileName(journal.DirectoryPath), export,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(export))),
            archiveBytes = new FileInfo(export).Length,
            packageBytes = new FileInfo(Path.Combine(journal.DirectoryPath, "replay.json")).Length,
            journalBytes = new FileInfo(Path.Combine(journal.DirectoryPath, "events.jsonl")).Length,
            boundaries = package.BoundaryCount, parentBoundaries = package.Parent?.Prefix.BoundaryCount ?? 0 };
    }
}
