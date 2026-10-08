using System.Text.Json.Nodes;
using CozyTown.Core.Town;
using NUnit.Framework;

namespace CozyTown.Core.Tests;

[TestFixture]
public sealed class TownWorldTests
{
    private static TownContent Content(bool obstacle = false, bool changed = false)
    {
        var places = new[]
        {
            new PlaceDefinition("a", "A", new TownPoint(80, 96), "outside"),
            new PlaceDefinition("b", "B", new TownPoint(120, 96), "outside"),
            new PlaceDefinition("c", "C", new TownPoint(80, 144), "outside"),
            new PlaceDefinition("d", "D", new TownPoint(120, 144), "outside"),
            new PlaceDefinition("meeting", "Meeting", new TownPoint(240, 160), "outside"),
            new PlaceDefinition("work", "Work", new TownPoint(changed ? 304 : 300, 80), "outside")
        };
        var residents = new[] { "mina", "eli", "ren", "sora" }.Select((id, i) => new ResidentDefinition(id, id, places[i].Id, places[i].Id, i % 2 == 0 ? "bread" : "apple",
            new Dictionary<string, int> { [i % 2 == 0 ? "apple" : "bread"] = 3 }, 5,
            [new("free", 0, 600, places[i].Id, "free", true), new("work", 600, 660, "work", "work", false), new("afternoon", 660, 1440, places[i].Id, "free", true)], "Wants to share surplus and meet neighbors.")).ToArray();
        return TownContent.Create(new TownManifest(384, 256, places, obstacle ? [new("wall", new TownRect(168, 24, 24, 152))] : [], [new("outside", new TownRect(0, 0, 384, 256))]), new TownDefinitions(residents, [new("apple", "Apple"), new("bread", "Bread")], 540));
    }

    private static TownWorld World(TownContent? content = null)
    {
        var world = new TownWorld(content ?? Content()); world.Advance(.001, 0); return world;
    }

    [TestCase(540, 4960, 664, "afternoon", 0)]
    [TestCase(1439, 80, 1441, "free", 1)]
    public void FixedHostStepsCrossScheduleAndDayBoundariesAfterSaveAndPause(int startMinute, int steps,
        double expectedMinute, string expectedSchedule, long expectedDay)
    {
        var originalContent = Content();
        var content = TownContent.Create(originalContent.Manifest, originalContent.Definitions with { StartMinute = startMinute });
        var world = new TownWorld(content);
        int saveStep = steps > 160 ? steps - 160 : steps / 2;
        for (int i = 0; i < steps; i++)
        {
            world.Advance(.025, .05);
            if (i % 100 == 0) world.AcknowledgeEvents(world.Events[^1].Sequence);
            if (i + 1 != saveStep) continue;
            world = TownWorld.RestoreCandidate(content, world.CaptureJson());
            double beforePause = world.WorldMinute;
            double acceptedBeforePause = world.State.AcceptedWorldMinutes;
            world.Advance(.025, .05, paused: true);
            Assert.That(world.WorldMinute, Is.EqualTo(beforePause));
            Assert.That(world.State.AcceptedWorldMinutes, Is.EqualTo(acceptedBeforePause));
        }
        Assert.That(world.WorldMinute, Is.EqualTo(expectedMinute).Within(1e-7));
        Assert.That(world.State.AcceptedWorldMinutes, Is.LessThan(1e-8));
        Assert.That(world.State.Residents.All(r => r.ScheduleId == expectedSchedule && r.ScheduleDay == expectedDay), Is.True);
        Assert.DoesNotThrow(() => TownWorld.RestoreCandidate(content, world.CaptureJson()));
    }

    [Test]
    public void PreviousClockNumericVersionIsRejectedWithoutChangingCurrentWorld()
    {
        var content = Content(); var world = new TownWorld(content);
        string before = TownJson.Serialize(world.State);
        var snapshot = JsonNode.Parse(world.CaptureJson())!;
        snapshot["numericVersion"] = "pixels-double-grid16-radius5-v1";
        Assert.Throws<InvalidDataException>(() => TownWorld.RestoreCandidate(content, snapshot.ToJsonString()));
        Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
    }

    private static DecisionRequest Request(TownWorld world, string id) => world.GetDecisionRequests().Single(r => r.ResidentId == id);
    private static DecisionResult Queue(TownWorld world, DecisionRequest request, ActionCandidate candidate, string source = "fixed_model")
    {
        var attempt = Guid.NewGuid().ToString("N");
        Assert.That(world.MarkDispatched(request.LogicalRequestId, attempt), Is.True);
        var result = new DecisionResult(world.SessionId, request.LogicalRequestId, attempt, request.ResidentId, candidate, source, null);
        Assert.That(world.ReceiveResult(result), Is.True); return result;
    }
    private static void Decide(TownWorld world, string id, ActionCandidate candidate)
    {
        Queue(world, Request(world, id), candidate); world.Advance(0, 0);
    }
    private static ActionCandidate Invite() => new("invite", "eli", "meeting", null, 0, 0, null, "一起聊聊吧。");
    private static ActionCandidate Accept() => new("respond", "mina", null, null, 0, 0, true, "好呀。");
    private static ActionCandidate Wait() => new("wait", null, null, null, 0, 0, null, "稍等。");
    private static void Meet(TownWorld world)
    {
        Decide(world, "mina", Invite()); Decide(world, "eli", Accept()); world.Advance(8, 0);
        Assert.That(world.State.Commitments.Single().Stage, Is.EqualTo("conversation"));
    }

    [Test]
    public void CanonicalFingerprintDoesNotDependOnAuthorTraversalOrder()
    {
        var content = Content(); var m = content.Manifest; var d = content.Definitions;
        var reordered = TownContent.Create(m with { Places = m.Places.Reverse().ToArray(), Regions = m.Regions.Reverse().ToArray() }, d with { Residents = d.Residents.Reverse().ToArray(), Resources = d.Resources.Reverse().ToArray() });
        Assert.That(reordered.Fingerprint, Is.EqualTo(content.Fingerprint));
    }

    [Test]
    public void DuplicateIdentityMissingReferenceAndOverlappingSchedulesFailBeforeWorldStarts()
    {
        var c = Content(); var m = c.Manifest; var d = c.Definitions;
        Assert.Throws<InvalidDataException>(() => TownContent.Create(m with { Places = [.. m.Places, m.Places[0]] }, d));
        Assert.Throws<InvalidDataException>(() => TownContent.Create(m with { Places = m.Places.Where(p => p.Id != "work").ToArray() }, d));
        d.Residents[0].Schedule[1] = d.Residents[0].Schedule[1] with { StartMinute = 500 };
        Assert.Throws<InvalidDataException>(() => TownContent.Create(m, d));
    }

    [Test]
    public void ObservationContainsOnlyVisibleNeighborsAndAnnouncedSurplusKinds()
    {
        var world = World(); var observation = world.Observe("mina");
        Assert.That(observation.Residents.Select(r => r.Id), Is.EquivalentTo(new[] { "eli", "ren", "sora" }));
        Assert.That(observation.Residents.Single(r => r.Id == "eli").OfferedResourceIds, Is.EqualTo(new[] { "bread" }));
        var json = TownJson.Serialize(observation);
        Assert.That(json, Does.Not.Contain("coins").And.Not.Contain("inventory"));
    }

    [Test]
    public void MovementQueriesCanBlockWithoutChangingCommittedPosition()
    {
        var content = Content(); var world = new TownWorld(content, movementQuery: (_, _, _) => false);
        world.Advance(.01, 0); var before = world.State.Residents.Single(r => r.Id == "mina").Position;
        world.Advance(60, 0);
        var resident = world.State.Residents.Single(r => r.Id == "mina");
        Assert.That(resident.Position, Is.EqualTo(before));
        Assert.That(resident.BlockedReason, Is.EqualTo("body_blocked"));
    }

    [Test]
    public void GridRouteAvoidsObstacleAndSnapshotContinuesTheSameWaypoints()
    {
        var content = Content(obstacle: true); var world = World(content);
        world.Advance(60, 0); world.Advance(.4, 0);
        var before = world.State.Residents.Single(r => r.Id == "mina");
        Assert.That(before.Route.Count, Is.GreaterThan(2));
        var restored = TownWorld.RestoreCandidate(content, world.CaptureJson());
        Assert.That(restored.State.Residents.Single(r => r.Id == "mina").Route, Is.EqualTo(before.Route));
        Assert.That(restored.State.Residents.Single(r => r.Id == "mina").RouteCursor, Is.EqualTo(before.RouteCursor));
        world.Advance(2, 0); restored.Advance(2, 0);
        Assert.That(restored.State.Residents.Select(r => r.Position), Is.EqualTo(world.State.Residents.Select(r => r.Position)));
    }

    [Test]
    public void CapturingSnapshotDoesNotAdvanceOrShareMutableState()
    {
        var world = World(); var before = world.State; var snapshot = world.CaptureSnapshot();
        snapshot.State.Residents[0].Coins = 99;
        Assert.That(world.State.Residents[0].Coins, Is.EqualTo(before.Residents[0].Coins));
        Assert.That(world.WorldMinute, Is.EqualTo(before.WorldMinute));
        Assert.That(world.State.Sequence, Is.EqualTo(before.Sequence));
    }

    [Test]
    public void InvalidSaveDoesNotAlterTheRunningWorld()
    {
        var world = World(); var before = world.CaptureSnapshot().State; var json = world.CaptureJson();
        Assert.Throws<InvalidDataException>(() => TownWorld.RestoreCandidate(Content(changed: true), json));
        var node = JsonNode.Parse(json)!; node["state"]!["playerPosition"]!.AsObject().Remove("x");
        Assert.Throws<InvalidDataException>(() => TownWorld.RestoreCandidate(Content(), node.ToJsonString()));
        Assert.That(TownJson.Serialize(world.State), Is.EqualTo(TownJson.Serialize(before)));
    }

    [Test]
    public void CorruptAssetsRouteAndSuspendedCommitmentAreRejected()
    {
        var content = Content(); var world = World(content); var original = world.CaptureJson();
        var node = JsonNode.Parse(original)!; node["state"]!["residents"]![0]!["coins"] = 99;
        Assert.Throws<InvalidDataException>(() => TownWorld.RestoreCandidate(content, node.ToJsonString()));
        node = JsonNode.Parse(original)!; node["state"]!["residents"]![0]!["routeCursor"] = 999;
        Assert.Throws<InvalidDataException>(() => TownWorld.RestoreCandidate(content, node.ToJsonString()));
        node = JsonNode.Parse(original)!; node["state"]!["residents"]![0]!["commitmentId"] = "missing";
        Assert.Throws<InvalidDataException>(() => TownWorld.RestoreCandidate(content, node.ToJsonString()));
    }

    [Test]
    public void PendingRestorePreservesLogicalIdentityFrozenInputAndRecoveryClassification()
    {
        var content = Content(); var world = World(content); var request = Request(world, "mina");
        var queued = TownWorld.RestoreCandidate(content, world.CaptureJson());
        Assert.That(queued.GetDecisionRequests().Single(r => r.ResidentId == "mina").IsRecovery, Is.False);
        world.MarkDispatched(request.LogicalRequestId, "attempt-original"); world.Advance(0, 13, true);
        var restored = TownWorld.RestoreCandidate(content, world.CaptureJson()); var recovered = restored.GetDecisionRequests().Single(r => r.ResidentId == "mina");
        Assert.Multiple(() =>
        {
            Assert.That(recovered.LogicalRequestId, Is.EqualTo(request.LogicalRequestId));
            Assert.That(recovered.InputJson, Is.EqualTo(request.InputJson));
            Assert.That(recovered.RemainingWaitSeconds, Is.EqualTo(47));
            Assert.That(recovered.IsRecovery, Is.True);
            Assert.That(recovered.AttemptId, Is.Null);
            Assert.That(restored.State.BranchId, Is.Not.EqualTo(world.State.BranchId));
        });
    }

    [Test]
    public void SealedResponseResumesWithoutResendingAndRejectsOldSessionCallback()
    {
        var content = Content(); var world = World(content); var request = Request(world, "mina"); var result = Queue(world, request, Invite());
        var restored = TownWorld.RestoreCandidate(content, world.CaptureJson());
        Assert.That(restored.GetDecisionRequests().Any(r => r.LogicalRequestId == request.LogicalRequestId), Is.False);
        Assert.That(restored.ReceiveResult(result), Is.False);
        restored.Advance(0, 0);
        Assert.That(restored.State.Commitments.Count, Is.EqualTo(1));
        Assert.That(restored.Events.Any(e => e.Reason == "retired_session"), Is.True);
    }

    [Test]
    public void PauseSealsTimelyResponseUntilResumeWithoutTimingItOutLater()
    {
        var world = World(); var request = Request(world, "mina");
        Queue(world, request, Invite()); var time = world.WorldMinute;
        foreach (var other in world.GetDecisionRequests()) Queue(world, other, Wait());
        world.Advance(10, 100, true);
        Assert.That(world.WorldMinute, Is.EqualTo(time)); Assert.That(world.State.Commitments, Is.Empty);
        world.Advance(0, 0);
        Assert.That(world.State.Commitments.Any(c => c.InitiatorId == "mina" && c.InviteeId == "eli"), Is.True);
    }

    [Test]
    public void DeadlineWinsOverResponseAtTheSameWorldBoundary()
    {
        var world = World(); var request = Request(world, "mina");
        world.MarkDispatched(request.LogicalRequestId, "deadline-attempt");
        world.Advance(600 - world.WorldMinute, 0);
        Assert.That(world.ReceiveResult(new(world.SessionId, request.LogicalRequestId, "deadline-attempt", "mina", Invite(), "fixed_model", null)), Is.False);
        Assert.That(world.State.Commitments, Is.Empty);
        Assert.That(world.Events.Any(e => e.ResidentId == "mina" && e.Reason == "schedule_boundary"), Is.True);
        Assert.That(world.State.Residents.All(r => r.ScheduleId == "work"), Is.True);
    }

    [Test]
    public void DayBoundaryContinuesSchedulesWithoutTeleporting()
    {
        var world = World(); world.Advance(900, 0);
        Assert.That(world.State.Residents.All(r => r.ScheduleDay == 1 && r.ScheduleId == "free"), Is.True);
        Assert.That(world.State.Residents.All(r => Content().IsWalkable(r.Position)), Is.True);
    }

    [Test]
    public void AcceptedInvitationRestoresMidRouteWithOriginalCommitment()
    {
        var content = Content(); var world = World(content);
        Decide(world, "mina", Invite()); Decide(world, "eli", Accept()); world.Advance(.2, 0);
        var snapshot = world.CaptureSnapshot(); var restored = TownWorld.RestoreCandidate(content, TownJson.Serialize(snapshot));
        Assert.That(restored.State.Commitments.Single().Id, Is.EqualTo(snapshot.State.Commitments.Single().Id));
        Assert.That(restored.State.Residents.Select(r => r.Position), Is.EqualTo(snapshot.State.Residents.Select(r => r.Position)));
        restored.Advance(8, 0);
        Assert.That(restored.State.Commitments.Single().Stage, Is.EqualTo("conversation"));
        Assert.That(restored.State.Residents.Single(r => r.Id == "mina").Position, Is.Not.EqualTo(restored.State.Residents.Single(r => r.Id == "eli").Position));
    }

    [Test]
    public void ResourceTransferRequiresAcceptanceAndIsAtomicAcrossSaveAndDuplicateResult()
    {
        var content = Content(); var world = World(content); Meet(world);
        var before = world.State;
        Decide(world, "mina", new ActionCandidate("propose_exchange", "eli", null, "apple", 1, 2, null, "用两枚金币换一个苹果？"));
        Assert.That(world.State.Residents.Select(r => r.Coins), Is.EqualTo(before.Residents.Select(r => r.Coins)));
        var request = Request(world, "eli"); var result = Queue(world, request, Accept()); world.Advance(0, 0);
        Assert.That(world.ReceiveResult(result), Is.False);
        var restored = TownWorld.RestoreCandidate(content, world.CaptureJson());
        Assert.That(restored.State.Residents.Single(r => r.Id == "eli").Inventory["apple"], Is.EqualTo(1));
        Assert.That(restored.State.Residents.Single(r => r.Id == "mina").Coins, Is.EqualTo(7));
        Assert.That(restored.State.Residents.Single(r => r.Id == "eli").Coins, Is.EqualTo(3));
        Assert.That(restored.State.ExecutedTransferIds.Count, Is.EqualTo(1));
    }

    [Test]
    public void InsufficientMoneyLeavesEveryAssetUnchanged()
    {
        var world = World(); Meet(world);
        Decide(world, "mina", new ActionCandidate("propose_exchange", "eli", null, "apple", 1, 20, null, "请考虑这个价格。"));
        var before = TownJson.Serialize(world.State.Residents.Select(r => new { r.Coins, r.Inventory }));
        Decide(world, "eli", Accept());
        Assert.That(TownJson.Serialize(world.State.Residents.Select(r => new { r.Coins, r.Inventory })), Is.EqualTo(before));
        Assert.That(world.Events.Any(e => e.Reason == "insufficient_assets"), Is.True);
    }

    [Test]
    public void BusyTargetAndUnobservedTargetCannotBeInvited()
    {
        var content = Content(); var definitions = content.Definitions;
        definitions.Residents.Single(r => r.Id == "eli").Schedule[0] = definitions.Residents.Single(r => r.Id == "eli").Schedule[0] with { IsFree = false };
        var world = World(TownContent.Create(content.Manifest, definitions));
        Decide(world, "mina", Invite());
        Assert.That(world.State.Commitments, Is.Empty);
        Assert.That(world.Events.Any(e => e.Reason == "target_schedule_busy"), Is.True);
    }

    [Test]
    public void TimedOutDecisionUsesExplicitRuleFallbackAndCannotAcceptLateReply()
    {
        var world = World(); var request = Request(world, "mina"); world.MarkDispatched(request.LogicalRequestId, "attempt");
        world.Advance(0, 61, true); world.Advance(0, 0);
        Assert.That(world.Events.Any(e => e.Kind == "rule_fallback" && e.Reason == "logical_timeout"), Is.True);
        Assert.That(world.ReceiveResult(new DecisionResult(world.SessionId, request.LogicalRequestId, "attempt", "mina", Wait(), "real_model", null)), Is.False);
    }

    [Test]
    public void FourResidentsEnterDecisionQueueAndDispatchCursorSurvivesRestore()
    {
        var content = Content(); var world = World(content); var requests = world.GetDecisionRequests();
        Assert.That(requests.Select(r => r.ResidentId).Distinct().Count(), Is.EqualTo(4));
        world.MarkDispatched(requests[0].LogicalRequestId, "first");
        var restored = TownWorld.RestoreCandidate(content, world.CaptureJson());
        Assert.That(restored.State.DecisionCursor, Is.EqualTo(world.State.DecisionCursor));
        Assert.That(restored.State.PendingDecisions.Select(p => p.Request.LogicalRequestId), Is.EqualTo(world.State.PendingDecisions.Select(p => p.Request.LogicalRequestId)));
    }

    [Test]
    public void AcknowledgingDurableEventsKeepsAContiguousSnapshotSuffix()
    {
        var content = Content(); var world = World(content);
        for (int i = 0; i < 200; i++) world.Advance(0, 0);
        var checkpoint = world.State.Sequence;
        world.AcknowledgeEvents(checkpoint);
        Assert.That(world.Events.Count, Is.EqualTo(128));
        var restored = TownWorld.RestoreCandidate(content, world.CaptureJson());
        Assert.That(restored.State.ForkSequence, Is.EqualTo(checkpoint));
    }

    [Test]
    public void OrdinaryResidentRulesReachAnAcceptedTradeWithoutAssignedPairs()
    {
        var world = World();
        for (int i = 0; i < 30 && world.State.ExecutedTransferIds.Count == 0; i++)
        {
            var request = world.GetDecisionRequests().FirstOrDefault();
            if (request is not null) Queue(world, request, world.CreateRuleCandidate(request), "rules");
            world.Advance(.5, 1);
        }
        Assert.That(world.State.ExecutedTransferIds.Count, Is.GreaterThan(0));
        Assert.That(world.Events.Any(e => e.Kind == "resource_transferred" && e.Source == "rules"), Is.True);
        Assert.That(world.State.Residents.Sum(r => r.Coins), Is.EqualTo(20));
    }

    [Test]
    public void ChangedPublicSurplusChangesTheIndependentRulesChosenNeighbor()
    {
        var baseline = Content(); var world = World(baseline);
        var first = world.CreateRuleCandidate(Request(world, "mina"));
        var definitions = baseline.Definitions;
        definitions.Residents.Single(r => r.Id == "eli").Inventory["bread"] = 0;
        var counterfactual = World(TownContent.Create(baseline.Manifest, definitions));
        var second = counterfactual.CreateRuleCandidate(Request(counterfactual, "mina"));
        Assert.That(first.TargetId, Is.EqualTo("eli"));
        Assert.That(second.TargetId, Is.EqualTo("sora"));
        Assert.That(counterfactual.Observe("mina").Residents.Single(r => r.Id == "eli").OfferedResourceIds, Is.Empty);
    }

    [TestCase("teleport", null)]
    [TestCase("invite", "unknown-resident")]
    public void IllegalCandidatesAreRejectedAndFallbackIsLabeled(string action, string? target)
    {
        var world = World(); Decide(world, "mina", new ActionCandidate(action, target, "meeting", null, 0, 0, null, "hello"));
        Assert.That(world.State.Commitments, Is.Empty);
        Assert.That(world.Events.Any(e => e.Kind == "candidate_rejected" && e.ResidentId == "mina"), Is.True);
        Assert.That(world.Events.Any(e => e.Kind == "rule_fallback" && e.ResidentId == "mina" && e.Source == "rule_fallback"), Is.True);
    }

    [Test]
    public void DuplicatedNumericJsonFieldsAndTamperedFrozenObservationFailRestore()
    {
        var content = Content(); var world = World(content); Request(world, "mina");
        var json = world.CaptureJson();
        var node = JsonNode.Parse(json)!;
        node["state"]!["pendingDecisions"]![0]!["observation"]!["worldMinute"] = 0;
        Assert.Throws<InvalidDataException>(() => TownWorld.RestoreCandidate(content, node.ToJsonString()));
        var duplicate = json.Replace("\"schemaVersion\": 4,", "\"schemaVersion\": 4, \"schemaVersion\": 4,");
        Assert.Throws<InvalidDataException>(() => TownWorld.RestoreCandidate(content, duplicate));
    }

    [Test]
    public void DefinitionViewsCannotMutateAuthoritativeContent()
    {
        var content = Content(); content.Resident("mina").Inventory["apple"] = 99;
        var definitions = content.Definitions; definitions.Residents[0] = definitions.Residents[0] with { Coins = 0 };
        Assert.That(new TownWorld(content).State.Residents.Single(r => r.Id == "mina").Inventory["apple"], Is.EqualTo(3));
    }

    [Test]
    public void SavingAnExpiredPausedRequestDoesNotGrantAnotherWaitBudget()
    {
        var content = Content(); var world = World(content); var request = world.GetDecisionRequests().First();
        world.Advance(0, 60, true);
        var restored = TownWorld.RestoreCandidate(content, world.CaptureJson());
        Assert.That(restored.GetDecisionRequests().Any(r => r.LogicalRequestId == request.LogicalRequestId), Is.False);
        restored.Advance(0, 0);
        Assert.That(restored.State.TerminalRequestIds, Does.Contain(request.LogicalRequestId));
        Assert.That(restored.Events.Any(e => e.Kind == "rule_fallback" && e.LogicalRequestId == request.LogicalRequestId && e.Reason == "logical_timeout"), Is.True);
    }

    [Test]
    public void InvisibleNeighborCannotLeakThroughAVisibilityWall()
    {
        var content = Content(); var geometry = content.Manifest;
        geometry = geometry with { Obstacles = [new("screen", new TownRect(96, 64, 8, 96))] };
        var world = World(TownContent.Create(geometry, content.Definitions));
        Assert.That(world.Observe("mina").Residents.Select(r => r.Id), Is.EqualTo(new[] { "ren" }));
        Assert.That(Request(world, "mina").AllowedTargets, Is.EqualTo(new[] { "ren" }));
    }

    [Test]
    public void InitiatorWithoutAnOfferGivesPartnerAConversationTurn()
    {
        var content = Content(); var definitions = content.Definitions;
        definitions.Residents.Single(r => r.Id == "eli").Inventory["bread"] = 1;
        var world = World(TownContent.Create(content.Manifest, definitions));
        for (int i = 0; i < 35 && world.State.ExecutedTransferIds.Count == 0; i++)
        {
            var request = world.GetDecisionRequests().FirstOrDefault();
            if (request is not null) Queue(world, request, world.CreateRuleCandidate(request), "rules");
            world.Advance(.5, 1);
        }
        Assert.That(world.Events.Any(e => e.ResidentId == "eli" && e.Kind == "candidate_executed" && e.Reason == "speak"), Is.True);
        Assert.That(world.Events.Any(e => e.Kind == "resource_transferred" && e.Source == "rules"), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void BodyArrivingAt59SecondsSurvivesDurableCompletionAt61Seconds(bool paused)
    {
        var content = Content(); var world = World(content); var requests = world.GetDecisionRequests(); var request = requests[0];
        world.MarkDispatched(request.LogicalRequestId, "arrival-attempt");
        foreach (var other in requests.Skip(1)) Queue(world, other, Wait());
        world.Advance(0, 0);
        world.Advance(0, 59, paused);
        Assert.That(world.MarkResponseArrived(world.SessionId, request.LogicalRequestId, "arrival-attempt", 59), Is.True);
        world.Advance(0, 2, paused);
        Assert.That(world.HasUncommittedResponses, Is.True);
        Assert.Throws<InvalidOperationException>(() => world.CaptureSnapshot());
        Assert.That(world.State.PendingDecisions.Single().SealedResult, Is.Null);
        Assert.That(world.State.PendingDecisions.Single().Request.RemainingWaitSeconds, Is.EqualTo(1));
        Assert.That(world.ReceiveResult(new DecisionResult(world.SessionId, request.LogicalRequestId, "arrival-attempt", request.ResidentId, Wait(), "real_model", null, 59)), Is.True);
        Assert.That(world.HasUncommittedResponses, Is.False);
        Assert.DoesNotThrow(() => TownWorld.RestoreCandidate(content, world.CaptureJson()));
        if (paused)
        {
            world.Advance(0, 0, true);
            Assert.That(world.Events.Any(e => e.Kind == "candidate_executed" && e.LogicalRequestId == request.LogicalRequestId), Is.False);
        }
        world.Advance(0, 0);
        Assert.That(world.Events.Any(e => e.Kind == "candidate_executed" && e.Source == "real_model" && e.LogicalRequestId == request.LogicalRequestId), Is.True);
        Assert.That(world.Events.Any(e => e.Kind == "rule_fallback" && e.Reason == "logical_timeout" && e.LogicalRequestId == request.LogicalRequestId), Is.False);
    }

    [Test]
    public void ArrivalAtDeadlineDoesNotFreezeTimeoutOrPermitPartialResponseSnapshot()
    {
        var content = Content(); var world = World(content); var request = world.GetDecisionRequests()[0];
        world.MarkDispatched(request.LogicalRequestId, "arrival-attempt");
        var before = world.CaptureSnapshot();
        Assert.That(world.MarkResponseArrived(world.SessionId, request.LogicalRequestId, "arrival-attempt", 60), Is.False);
        Assert.That(world.MarkResponseArrived("old-session", request.LogicalRequestId, "arrival-attempt", 59), Is.False);
        Assert.That(world.HasUncommittedResponses, Is.False);
        world.Advance(0, 61, true);
        Assert.That(world.State.PendingDecisions.First().SealedResult?.Error, Is.EqualTo("logical_timeout"));
        before.State.PendingDecisions.First().ResponseArrivalSeconds = 59;
        Assert.Throws<InvalidDataException>(() => TownWorld.RestoreCandidate(content, TownJson.Serialize(before)));
    }

    [Test]
    public void TimelyArrivalMarkerCanReplaceAnUnconsumedPausedTimeout()
    {
        var world = World(); var request = world.GetDecisionRequests()[0];
        world.MarkDispatched(request.LogicalRequestId, "arrival-attempt");
        world.Advance(0, 61, true);
        Assert.That(world.MarkResponseArrived(world.SessionId, request.LogicalRequestId, "arrival-attempt", 59), Is.True);
        Assert.That(world.State.PendingDecisions.First().SealedResult, Is.Null);
        Assert.That(world.ReceiveResult(new DecisionResult(world.SessionId, request.LogicalRequestId, "arrival-attempt", request.ResidentId, Wait(), "real_model", null, 59)), Is.True);
        world.Advance(0, 0);
        Assert.That(world.Events.Any(e => e.Kind == "candidate_executed" && e.LogicalRequestId == request.LogicalRequestId), Is.True);
    }

    [Test]
    public void TimelyBodyArrivalCannotReviveARequestAfterItsWorldScheduleEnds()
    {
        var world = World(); var request = world.GetDecisionRequests()[0];
        world.MarkDispatched(request.LogicalRequestId, "arrival-attempt");
        world.MarkResponseArrived(world.SessionId, request.LogicalRequestId, "arrival-attempt", 59);
        world.Advance(600 - world.WorldMinute, 2);
        Assert.That(world.HasUncommittedResponses, Is.False);
        Assert.That(world.ReceiveResult(new DecisionResult(world.SessionId, request.LogicalRequestId, "arrival-attempt", request.ResidentId, Wait(), "real_model", null, 59)), Is.False);
        Assert.That(world.Events.Any(e => e.Reason == "schedule_boundary" && e.LogicalRequestId == request.LogicalRequestId), Is.True);
    }
}
