using System.Text.Json;
using CozyTown.Core.Town;
using CozyTown.Model;
using Godot;
using File = System.IO.File;

namespace CozyTown.Game;

public partial class TownGame
{
    private bool _experimentStopping;
    private string? _experimentStopReason;
    private double _experimentDrainStarted;

    private async Task RunSceneSmokeAsync()
    {
        _finishing = true;
        var checks = new List<object>();
        void Check(string name, bool passed, object? evidence = null)
        {
            checks.Add(new { name, passed, evidence });
            if (!passed) throw new InvalidDataException("Scene verification failed: " + name);
        }
        try
        {
            if (_mode != "rules" || _model is not null) throw new InvalidDataException("Offline scene verification requires rules mode.");
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            await RunContentMutationChecksAsync(Check);
            Check("resident_body_bindings", _actors.Count == 4 && _actors.Keys.Order().SequenceEqual(_loaded.Definitions.Residents.Select(r => r.Id).Order()));
            Check("authoritative_places_clear_in_godot", _loaded.Manifest.Places.All(p => CheckMotion("validation", p.Position, p.Position)), new { count = _loaded.Manifest.Places.Length });
            var obstacleChecks = _loaded.Manifest.Obstacles.Select(o => new
            {
                o.Id,
                blocked = !CheckMotion("validation", new(o.Bounds.X + o.Bounds.Width / 2, o.Bounds.Y + o.Bounds.Height / 2), new(o.Bounds.X + o.Bounds.Width / 2, o.Bounds.Y + o.Bounds.Height / 2))
            }).ToArray();
            Check("scene_obstacle_bodies", obstacleChecks.Length > 0 && obstacleChecks.All(o => o.blocked), obstacleChecks);
            var initial = _world.GetView();
            var firstObstacle = _loaded.Manifest.Obstacles[0].Bounds;
            var blockedPoint = new TownPoint(firstObstacle.X + firstObstacle.Width / 2, firstObstacle.Y + firstObstacle.Height / 2);
            Check("player_collision_rejection", !_world.SetPlayerPosition(blockedPoint) && _world.GetView().PlayerPosition == initial.PlayerPosition);
            var playerStep = new[] { new TownPoint(8, 0), new TownPoint(-8, 0), new TownPoint(0, 8), new TownPoint(0, -8) }
                .Select(d => new TownPoint(initial.PlayerPosition.X + d.X, initial.PlayerPosition.Y + d.Y))
                .First(p => _loaded.Content.CanMove(initial.PlayerPosition, p) && CheckMotion("player", initial.PlayerPosition, p));
            Check("player_committed_movement", _world.SetPlayerPosition(playerStep) && _world.GetView().PlayerPosition == playerStep);
            var playerDestination = _loaded.Manifest.Places.Where(p => p.Position.DistanceTo(playerStep) > 64)
                .OrderBy(p => p.Position.DistanceTo(playerStep)).First();
            _playerRoute = _loaded.Content.FindRoute(playerStep, playerDestination.Position) ?? throw new InvalidDataException("Player verification route is unreachable.");
            _playerRouteCursor = 0;
            AdvancePlayer(.1);
            _world.Advance(.25, .5);
            FlushWorldEvents();
            _view = _world.GetView();
            var savedView = _view;
            var savedPlayerRoute = _playerRoute.ToArray();
            int savedPlayerCursor = _playerRouteCursor;
            Check("save_during_routes", savedView.Residents.Any(r => r.RouteCursor < r.Route.Count) && savedView.Residents.All(r => r.Position != initial.Residents.Single(p => p.Id == r.Id).Position));
            SaveWorld();
            Check("published_save_readable", File.Exists(_savePath));
            var diskSave = TownSaveFile.Read(_savePath);
            Check("save_belongs_to_current_branch", TownJson.Deserialize<TownSnapshot>(diskSave.WorldJson).State.BranchId == savedView.BranchId);
            AdvancePlayer(.1);
            _world.Advance(.5, 1);
            FlushWorldEvents();
            string previousSession = _world.SessionId;
            LoadWorld();
            _view = _world.GetView();
            Check("route_restore_preserves_positions_and_cursors", !_fatal && _world.SessionId != previousSession && _view.Residents.All(r =>
            {
                var expected = savedView.Residents.Single(p => p.Id == r.Id);
                return r.Position == expected.Position && r.RouteCursor == expected.RouteCursor && r.Route.SequenceEqual(expected.Route) && r.Activity == expected.Activity;
            }), new { sourceBranch = savedView.BranchId, restoredBranch = _view.BranchId, diskSave.JournalRecordCount });
            Check("player_click_route_restored", _playerRoute is not null && _playerRoute.SequenceEqual(savedPlayerRoute)
                && _playerRouteCursor == savedPlayerCursor && _view.PlayerPosition == savedView.PlayerPosition);
            AdvancePlayer(.1);
            Check("player_click_route_continues", _world.GetView().PlayerPosition != savedView.PlayerPosition);
            SetPause("smoke_menu", true); SetPause("smoke_focus", true);
            double pausedTime = _world.WorldMinute;
            _world.Advance(5, .5, _pauseReasons.Count > 0);
            SetPause("smoke_menu", false);
            _world.Advance(5, .5, _pauseReasons.Count > 0);
            Check("stacked_pause_keeps_world_frozen", _world.WorldMinute == pausedTime && _pauseReasons.Contains("smoke_focus"));
            SetPause("smoke_focus", false);
            _world.Advance(.1, .2, _pauseReasons.Count > 0);
            Check("resume_advances_world", _world.WorldMinute > pausedTime);
            bool savedSocialState = false;
            for (int step = 0; step < 180; step++)
            {
                DispatchDecisions();
                _world.Advance(.1, .2);
                FlushWorldEvents();
                _view = _world.GetView();
                if (!savedSocialState && _view.Commitments.Any(c => c.Stage is "offered" or "travelling" or "conversation" or "exchange_offered"))
                {
                    var commitments = _view.Commitments.Select(c => new { c.Id, c.Stage, c.TurnNumber, c.TurnResidentId }).ToArray();
                    var pendingIds = _view.PendingDecisions.Select(p => p.Request.LogicalRequestId).ToArray();
                    SaveWorld(); LoadWorld();
                    var restored = _world.GetView();
                    Check("social_state_continues_same_commitments", !_fatal && TownJson.Serialize(restored.Commitments.Select(c => new { c.Id, c.Stage, c.TurnNumber, c.TurnResidentId }).ToArray()) == TownJson.Serialize(commitments)
                        && restored.PendingDecisions.Select(p => p.Request.LogicalRequestId).SequenceEqual(pendingIds));
                    savedSocialState = true;
                }
            }
            FlushWorldEvents();
            _view = _world.GetView();
            Check("four_residents_moved_through_scene", _view.Residents.All(r => r.Position != initial.Residents.Single(p => p.Id == r.Id).Position));
            Check("four_independent_rule_decisions", _loaded.Definitions.Residents.All(r => _experimentEvents.Any(e => e.ResidentId == r.Id && e.Kind == "decision_created")));
            Check("ordinary_rules_created_social_activity", savedSocialState && _experimentEvents.Any(e => e.Kind == "conversation_started"));
            Check("assets_conserved", _view.Residents.Sum(r => r.Coins) == _loaded.Definitions.Residents.Sum(r => r.Coins)
                && _loaded.Definitions.Resources.All(resource => _view.Residents.Sum(r => r.Inventory.GetValueOrDefault(resource.Id)) == _loaded.Definitions.Residents.Sum(r => r.Inventory.GetValueOrDefault(resource.Id))));
            SyncActors(); RefreshInterface();
            string? screenshot = await CaptureVerificationScreenshotAsync();
            var nextBusyMinute = _loaded.Definitions.Residents.SelectMany(r => r.Schedule).Where(s => !s.IsFree && s.StartMinute > _world.WorldMinute % 1440).Min(s => s.StartMinute);
            _world.Advance(nextBusyMinute - _world.WorldMinute % 1440 + .01, 0);
            FlushWorldEvents();
            Check("schedule_boundary_preempts_social_activity", _world.GetView().Residents.All(r => r.CommitmentId is null));
            _view = _world.GetView();
            SaveWorld();
            var faultSource = _world.GetView();
            var retiredGate = _dispatchGate;
            SetPause("smoke_preserved_menu", true);
            _fatal = true; _failure = "injected_recoverable_fault"; _dispatchGate.Stop(); _pauseReasons.Add("fault");
            LoadWorld();
            Check("fault_restore_rebuilds_dispatch_and_preserves_other_pause", !_fatal && _failure is null && !ReferenceEquals(retiredGate, _dispatchGate)
                && !_pauseReasons.Contains("fault") && !_pauseReasons.Contains("loading") && _pauseReasons.Contains("smoke_preserved_menu")
                && _world.GetView().Residents.Select(r => r.Position).SequenceEqual(faultSource.Residents.Select(r => r.Position)));
            SetPause("smoke_preserved_menu", false);
            double beforeFaultResume = _world.WorldMinute;
            _world.Advance(.1, .2, _pauseReasons.Count > 0);
            FlushWorldEvents();
            Check("fault_restore_resumes_world", _world.WorldMinute > beforeFaultResume);
            _journal.WriteArtifact("verification-final-state.json", CaptureHostSnapshot());
            var report = new
            {
                schemaVersion = 1, test = "godot_slice_smoke", passed = true, scenario = _scenario,
                headless = _headless, mode = _mode, residents = _actors.Count,
                saveReloadVerified = true, pauseVerified = true, collisionVerified = true,
                runtimeAssemblyLocation = typeof(object).Assembly.Location,
                screenshotStatus = screenshot is not null ? "captured" : _headless && Argument("--screenshot=") is not null ? "unsupported_headless" : "not_requested",
                screenshotPath = screenshot,
                engine = Engine.GetVersionInfo()["string"].AsString(), contentFingerprint = _loaded.Content.Fingerprint,
                checks, screenshot, runDirectories = _journals.Select(j => j.DirectoryPath).ToArray(),
                eventCount = _experimentEvents.Count, transfers = _experimentEvents.Where(e => e.Kind == "resource_transferred").ToArray(),
                coverage = "Actual Godot collision queries, ordinary rule decisions, world movement, file save/restore and pause behavior. Real-provider and user visual acceptance are separate."
            };
            _journal.WriteArtifact("verification-report.json", report);
            WriteReport(report);
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            var report = new { schemaVersion = 1, test = "godot_slice_smoke", passed = false, scenario = _scenario, checks, error = error.GetType().Name, message = error.Message };
            WriteReport(report);
            GD.PushError("Slice smoke failed: " + error.Message);
            GetTree().Quit(1);
        }
    }

    private void CheckExperimentCompletion()
    {
        if (_finishing || _model is null) return;
        BudgetSnapshot budget = _model.Budget;
        int stageAttempts = budget.Attempts.Count(a => a.Stage == _scenario);
        if (!_experimentStopping && (_elapsed >= 180 || stageAttempts >= 22 || budget.StopReason is not null))
        {
            _experimentStopping = true;
            _experimentDrainStarted = _elapsed;
            _experimentStopReason = budget.StopReason is not null ? "budget_stopped:" + budget.StopReason : stageAttempts >= 22 ? "stage_attempt_limit" : "180_second_horizon";
            _journal.Append("experiment_dispatch_stopped", new { reason = _experimentStopReason, elapsedSeconds = _elapsed, stageAttempts, budget });
        }
        if (!_experimentStopping) return;
        if (_activeModelRequests.Count > 0 && _elapsed - _experimentDrainStarted < 65) return;
        _world.Advance(0, 0, _pauseReasons.Count > 0);
        FlushWorldEvents();
        _finishing = true;
        _ = FinishRealExperimentAsync(budget, _activeModelRequests.Count == 0);
    }

    private async Task RunContentMutationChecksAsync(Action<string, bool, object?> check)
    {
        static IEnumerable<Node> Descendants(Node parent)
        {
            foreach (var child in parent.GetChildren())
            {
                yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }
        bool Rejected()
        {
            try { TownContentLoader.Load(_map, _scenario); return false; }
            catch (InvalidDataException) { return true; }
        }
        Node2D shop = Descendants(_map).OfType<Node2D>().Single(n => n.HasMeta("role") && n.GetMeta("role").AsString() == "place" && n.GetMeta("id").AsString() == "shop");
        Node originalParent = shop.GetParent();
        int originalIndex = shop.GetIndex();
        StringName originalName = shop.Name;
        Transform2D originalTransform = shop.Transform;
        var holder = new Node2D { Name = "VerificationHolder", Position = new Vector2(9, 13) };
        _map.AddChild(holder);
        try
        {
            shop.Name = "RenamedEntranceForVerification";
            originalParent.MoveChild(shop, 0);
            check("node_rename_and_reorder_preserve_fingerprint", TownContentLoader.Load(_map, _scenario).Content.Fingerprint == _loaded.Content.Fingerprint, null);
            shop.Reparent(holder, true);
            check("reparent_preserving_global_transform_preserves_fingerprint", TownContentLoader.Load(_map, _scenario).Content.Fingerprint == _loaded.Content.Fingerprint, null);
        }
        finally
        {
            if (shop.GetParent() != originalParent) shop.Reparent(originalParent, true);
            shop.Transform = originalTransform; shop.Name = originalName; originalParent.MoveChild(shop, originalIndex);
            holder.Free();
        }
        Node duplicate = shop.Duplicate();
        originalParent.AddChild(duplicate);
        try { check("duplicate_scene_identity_rejected", Rejected(), null); }
        finally { duplicate.Free(); }
        originalParent.RemoveChild(shop);
        try { check("missing_referenced_place_rejected", Rejected(), null); }
        finally { originalParent.AddChild(shop); shop.Transform = originalTransform; originalParent.MoveChild(shop, originalIndex); }
        try
        {
            shop.Rotation = .2f;
            check("unsupported_rotation_rejected", Rejected(), null);
            shop.Transform = originalTransform;
            shop.Scale = new Vector2(1.1f, 1);
            check("unsupported_scale_rejected", Rejected(), null);
        }
        finally { shop.Transform = originalTransform; }
        var unregisteredBody = new StaticBody2D { Name = "VerificationUnregisteredCollider", CollisionLayer = 1, CollisionMask = 0 };
        unregisteredBody.AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = new Vector2(8, 8) } });
        _map.AddChild(unregisteredBody);
        try { check("unregistered_world_collision_body_rejected", Rejected(), null); }
        finally { unregisteredBody.Free(); }
        var registeredObstacle = Descendants(_map).OfType<StaticBody2D>().First(n => n.HasMeta("role") && n.GetMeta("role").AsString() == "obstacle");
        uint originalLayer = registeredObstacle.CollisionLayer;
        try
        {
            registeredObstacle.CollisionLayer = 0;
            check("registered_obstacle_wrong_collision_layer_rejected", Rejected(), null);
        }
        finally { registeredObstacle.CollisionLayer = originalLayer; }
        Transform2D originalMapTransform = _map.Transform;
        try
        {
            _map.Position += new Vector2(1, 0);
            check("nonidentity_map_world_transform_rejected", Rejected(), null);
        }
        finally { _map.Transform = originalMapTransform; }
        var building = originalParent as Node2D ?? throw new InvalidDataException("Shop place has no movable building parent.");
        Vector2 originalBuildingPosition = building.Position;
        var connectedRoads = Descendants(_map).OfType<Line2D>().Where(line => line.HasMeta("role") && line.GetMeta("role").AsString() == "road"
            && (line.GetMeta("from_id").AsString() == "shop" || line.GetMeta("to_id").AsString() == "shop")).ToArray();
        var originalRoadPoints = connectedRoads.ToDictionary(line => line, line => line.Points.ToArray());
        var footprint = Descendants(building).OfType<StaticBody2D>().First(n => n.HasMeta("role") && n.GetMeta("role").AsString() == "obstacle");
        string footprintId = footprint.GetMeta("id").AsString();
        TownRect originalBounds = _loaded.Manifest.Obstacles.Single(o => o.Id == footprintId).Bounds;
        TownPoint originalShopPosition = _loaded.Content.Place("shop").Position;
        try
        {
            building.Position += new Vector2(8, 0);
            foreach (var road in connectedRoads)
            {
                Vector2[] points = road.Points.ToArray();
                if (road.GetMeta("from_id").AsString() == "shop") points[0] = road.ToLocal(shop.GlobalPosition);
                if (road.GetMeta("to_id").AsString() == "shop") points[^1] = road.ToLocal(shop.GlobalPosition);
                road.Points = points;
            }
            var moved = TownContentLoader.Load(_map, _scenario);
            TownPoint newShopPosition = moved.Content.Place("shop").Position;
            TownRect movedBounds = moved.Manifest.Obstacles.Single(o => o.Id == footprintId).Bounds;
            check("moving_shop_updates_shared_geometry_and_fingerprint", moved.Content.Fingerprint != _loaded.Content.Fingerprint
                && newShopPosition == new TownPoint(originalShopPosition.X + 8, originalShopPosition.Y)
                && movedBounds == originalBounds with { X = originalBounds.X + 8 }, new { from = originalShopPosition, to = newShopPosition, footprintId });
            var shopSchedules = moved.Definitions.Residents.SelectMany(r => r.Schedule.Where(s => s.PlaceId == "shop").Select(s => new { residentId = r.Id, schedule = s })).ToArray();
            var scheduleWorld = new TownWorld(moved.Content, _scenario);
            double firstShopTime = shopSchedules.Where(s => s.schedule.StartMinute >= moved.Definitions.StartMinute).Min(s => s.schedule.StartMinute);
            scheduleWorld.Advance(firstShopTime - scheduleWorld.WorldMinute, 0);
            check("shop_schedule_keeps_identity_and_uses_new_anchor", shopSchedules.Length > 0 && shopSchedules.Where(s => s.schedule.StartMinute == firstShopTime).All(s =>
            {
                var resident = scheduleWorld.GetView().Residents.Single(r => r.Id == s.residentId);
                return resident.DestinationPlaceId == "shop" && resident.Route.Count > 0 && resident.Route[^1] == newShopPosition;
            }), null);
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            var center = new TownPoint(movedBounds.X + movedBounds.Width / 2, movedBounds.Y + movedBounds.Height / 2);
            var vacated = new TownPoint(originalBounds.X + 1, originalBounds.Y + originalBounds.Height / 2);
            check("moved_shop_matches_actual_godot_collision", CheckMotion("validation", newShopPosition, newShopPosition)
                && !CheckMotion("validation", center, center)
                && (!_loaded.Content.Manifest.Obstacles.Any(o => o.Id != footprintId && o.Bounds.Contains(vacated, TownContent.BodyRadius)) ? CheckMotion("validation", vacated, vacated) : true), null);
        }
        finally
        {
            building.Position = originalBuildingPosition;
            foreach (var pair in originalRoadPoints) pair.Key.Points = pair.Value;
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        check("mutation_checks_restore_original_content", TownContentLoader.Load(_map, _scenario).Content.Fingerprint == _loaded.Content.Fingerprint, null);
    }

    private async Task FinishRealExperimentAsync(BudgetSnapshot budget, bool drained)
    {
        try
        {
            var residents = _loaded.Definitions.Residents.Select(r => new
            {
                r.Id,
                actualModelCompletions = _modelCompletions.Where(c => c.Dispatch.Request.Identity.ResidentId == r.Id).ToArray(),
                realExecutedChoices = _experimentEvents.Where(e => e.ResidentId == r.Id && e.Source == "real_model" && e.Kind == "candidate_executed").ToArray(),
                rejectedChoices = _experimentEvents.Where(e => e.ResidentId == r.Id && e.Kind is "candidate_rejected" or "response_rejected").ToArray(),
                fallbacks = _experimentEvents.Where(e => e.ResidentId == r.Id && e.Kind == "rule_fallback").ToArray()
            }).ToArray();
            bool coveragePassed = residents.All(r => r.realExecutedChoices.Length > 0);
            var missing = residents.Where(r => r.realExecutedChoices.Length == 0).Select(r => r.Id).ToArray();
            _view = _world.GetView(); SyncActors(); RefreshInterface();
            string? screenshot = await CaptureVerificationScreenshotAsync();
            budget = _model!.Budget;
            var report = new
            {
                schemaVersion = 1, test = "four_resident_real_model", passed = coveragePassed && drained && budget.SlotsHeld == 0 && budget.StopReason is null,
                fourResidentCoveragePassed = coveragePassed, missingResidents = missing, scenario = _scenario,
                stopReason = _experimentStopReason, elapsedSeconds = _elapsed, worldMinute = _world.WorldMinute,
                activeRequestsDrained = drained, remainingActiveRequests = _activeModelRequests.ToArray(),
                pausedReasons = _pauseReasons.Order().ToArray(), contentFingerprint = _loaded.Content.Fingerprint,
                residents, completions = _modelCompletions.ToArray(), budget,
                allRejections = _experimentEvents.Where(e => e.Kind is "candidate_rejected" or "response_rejected").ToArray(),
                allFallbacks = _experimentEvents.Where(e => e.Kind == "rule_fallback").ToArray(),
                meetings = _experimentEvents.Where(e => e.Kind is "invitation_created" or "meeting_accepted" or "conversation_started" or "meeting_ended").ToArray(),
                transfers = _experimentEvents.Where(e => e.Kind == "resource_transferred").ToArray(),
                screenshot, runDirectory = _journal.DirectoryPath,
                headless = _headless, mode = _mode, runtimeAssemblyLocation = typeof(object).Assembly.Location,
                screenshotStatus = screenshot is not null ? "captured" : _headless && Argument("--screenshot=") is not null ? "unsupported_headless" : "not_requested",
                screenshotPath = screenshot,
                coverage = "Every recorded request, failure and fallback remains in the denominator. This report checks actual model choices accepted by the world; it does not establish long-term success rate or user visual acceptance."
            };
            _journal.WriteArtifact("experiment-final-state.json", CaptureHostSnapshot());
            _journal.WriteArtifact("budget-at-finish.json", budget);
            _journal.WriteArtifact("experiment-report.json", report);
            WriteReport(report);
            GetTree().Quit(report.passed ? 0 : 1);
        }
        catch (Exception error)
        {
            WriteReport(new { schemaVersion = 1, test = "four_resident_real_model", passed = false, error = error.GetType().Name, drained, stage = _scenario });
            GetTree().Quit(1);
        }
    }

    private async Task<string?> CaptureVerificationScreenshotAsync()
    {
        string? path = Argument("--screenshot=");
        if (path is null) return null;
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Screenshot requires an absolute path.");
        if (_headless) return null;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using Image screenshot = GetViewport().GetTexture().GetImage();
        Error result = screenshot.SavePng(path);
        if (result != Error.Ok) throw new IOException("Viewport screenshot write failed: " + result);
        return path;
    }

    private void WriteFailureReport()
    {
        _experimentStopping = true;
        try
        {
            BudgetSnapshot? budget = null;
            string? budgetReadFailure = null;
            try { budget = _model?.Budget; }
            catch (Exception error) { budgetReadFailure = error.GetType().Name; }
            try { FlushWorldEvents(); }
            catch { }
            var report = new
            {
                schemaVersion = 1, test = _arguments.Contains("--real-experiment") ? "four_resident_real_model" : "godot_slice_smoke",
                passed = false, scenario = _scenario, mode = _mode, headless = _headless,
                failure = _failure, elapsedSeconds = _elapsed, budget, budgetReadFailure,
                activeRequests = _activeModelRequests.ToArray(), completions = _modelCompletions.ToArray(),
                events = _experimentEvents.ToArray(), runtimeAssemblyLocation = typeof(object).Assembly.Location,
                recordInterrupted = _journal?.Failed ?? true,
                screenshotStatus = "not_captured_after_failure", screenshotPath = (string?)null
            };
            try { _journal?.WriteArtifact("failure-report.json", report); }
            catch { }
            WriteReport(report);
        }
        catch (Exception error) { GD.PushError("Verification failure report could not be written: " + error.GetType().Name); }
        finally { GetTree().Quit(1); }
    }
}
