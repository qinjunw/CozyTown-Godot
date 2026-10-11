using CozyTown.Core.Town;
using NUnit.Framework;
using System.Text.Json.Nodes;

namespace CozyTown.Core.Tests;

[TestFixture]
public sealed class TownReplayModelBindingTests
{
    private const string Attempt = "00000000-0000-0000-0000-000000000001";

    [TestCase(null)]
    [TestCase("authentication_failed")]
    public void RealResponseWithoutAnAdmittedBudgetAttemptStopsAtItsFirstModelBoundary(string? stopReason)
    {
        var fixture = new Fixture(admitted: false, stopReason: stopReason);
        fixture.Complete();

        var report = TownReplay.Run(fixture.Recording.Complete());

        Assert.That(report.Passed, Is.False);
        Assert.That(report.Code, Is.EqualTo("divergence"));
        Assert.That(report.Boundary, Is.EqualTo(2));
        Assert.That(report.Command, Is.EqualTo("model_response_arrived"));
        Assert.That(report.Field, Is.EqualTo("model.budgetAttempt"));
        Assert.That(report.InputJson, Does.Contain(Attempt));
    }

    [Test]
    public void NpcArrivalCannotReplaceALateTransportArrivalWithATimelyWorldArrival()
    {
        var fixture = new Fixture();
        fixture.Complete(transportSeconds: 63, worldSeconds: 2);

        var report = TownReplay.Run(fixture.Recording.Complete());

        Assert.That(report.Passed, Is.False);
        Assert.That(report.Boundary, Is.EqualTo(3));
        Assert.That(report.Command, Is.EqualTo("arrival"));
        Assert.That(report.Field, Is.EqualTo("model.npcArrivalSeconds"));
        Assert.That(report.Expected, Is.EqualTo("63"));
        Assert.That(report.Actual, Is.EqualTo("2"));
    }

    [TestCase(null)]
    [TestCase("authentication_failed")]
    public void AlreadyAdmittedNpcResponseCanFinishAfterTheLedgerHasStopped(string? stopReason)
    {
        var fixture = new Fixture(stopReason: stopReason);
        fixture.Complete();
        var report = TownReplay.Run(fixture.Recording.Complete());
        Assert.That(report.Passed, Is.True, TownJson.Serialize(report));
    }

    [TestCase("runId", "wrong-run")]
    [TestCase("residentId", "eli")]
    [TestCase("activityRevision", 999)]
    [TestCase("kind", "player_text")]
    [TestCase("playerSessionId", "invented-player-session")]
    public void CurrentNpcDispatchMustDescribeTheRecordedWorldRequest(string field, object value)
    {
        var fixture = new Fixture(); fixture.Complete();
        var package = AlterDispatches(fixture.Recording.Complete(), node => node["request"]!["identity"]![field] = JsonValue.Create(value));
        var report = TownReplay.Run(package);
        Assert.That(report.Passed, Is.False);
        Assert.That(report.Boundary, Is.EqualTo(2));
        Assert.That(report.Field, Is.EqualTo("model." + field));
    }

    [Test]
    public void NpcResultMustKeepTheRecordedTransportArrivalTime()
    {
        var fixture = new Fixture(); fixture.Complete(resultSeconds: 3);
        var report = TownReplay.Run(fixture.Recording.Complete());
        Assert.That(report.Passed, Is.False);
        Assert.That(report.Command, Is.EqualTo("result"));
        Assert.That(report.Field, Is.EqualTo("model.npcArrivalSeconds"));
    }

    [Test]
    public void CurrentNpcResultWithACompleteTransportBodyRequiresTheWorldArrivalBoundary()
    {
        var fixture = new Fixture(); fixture.Complete(worldArrival: false);
        var report = TownReplay.Run(fixture.Recording.Complete());
        Assert.That(report.Passed, Is.False);
        Assert.That(report.Command, Is.EqualTo("result"));
        Assert.That(report.Field, Is.EqualTo("model.npcArrival"));
    }

    [Test]
    public void NpcDispatchCannotReplaceTheHostFrozenProviderInput()
    {
        var fixture = new Fixture(freeze: true); fixture.Complete();
        var package = AlterDispatches(fixture.Recording.Complete(), node => node["request"]!["inputJson"] = "changed provider request");
        var report = TownReplay.Run(package);
        Assert.That(report.Passed, Is.False);
        Assert.That(report.Boundary, Is.EqualTo(3));
        Assert.That(report.Field, Is.EqualTo("model.frozenRequest.inputJson"));
    }

    [Test]
    public void DispatchBudgetSnapshotMustBelongToTheReplayedLedger()
    {
        var fixture = new Fixture(); fixture.Complete();
        var package = AlterDispatches(fixture.Recording.Complete(), node => node["budget"]!["budgetId"] = "another-budget");
        var report = TownReplay.Run(package);
        Assert.That(report.Passed, Is.False);
        Assert.That(report.Boundary, Is.EqualTo(2));
        Assert.That(report.Field, Is.EqualTo("budget.budgetId"));
    }

    [TestCase("logicalRequestId", "another-request", "model.logicalRequestId")]
    [TestCase("stage", 1, "model.stage")]
    [TestCase("isRecovery", true, "model.isRecovery")]
    public void FrozenRequestMustKeepTheAdmittedAttemptOwnership(string field, object value, string expectedField)
    {
        var fixture = new Fixture(); fixture.Complete();
        var package = AlterDispatches(fixture.Recording.Complete(), node =>
        {
            var owner = field == "logicalRequestId" ? node["request"]!["identity"]! : node["request"]!;
            owner[field] = JsonValue.Create(value);
        });
        var report = TownReplay.Run(package);
        Assert.That(report.Passed, Is.False);
        Assert.That(report.Boundary, Is.EqualTo(2));
        Assert.That(report.Field, Is.EqualTo(expectedField));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NpcArrivalAndPersistenceAcceptBothRecordedArrivalOrders(bool worldArrivalFirst)
    {
        var fixture = new Fixture(); fixture.Complete(worldArrivalFirst: worldArrivalFirst, persistenceDelay: 100);
        var report = TownReplay.Run(fixture.Recording.Complete());
        Assert.That(report.Passed, Is.True, TownJson.Serialize(report));
    }

    [TestCase(false, "request_timeout")]
    [TestCase(true, "authentication_failed")]
    public void FailedNpcCompletionRetainsItsBudgetAndIdentityBindings(bool responseArrived, string failure)
    {
        var fixture = new Fixture(); fixture.CompleteFailure(responseArrived, failure);
        var report = TownReplay.Run(fixture.Recording.Complete());
        Assert.That(report.Passed, Is.True, TownJson.Serialize(report));
    }

    [TestCase("full-demo-v1")]
    [TestCase("full-demo-v2")]
    public void RegisteredFullDemoProfilesBindTheirOwnAdmittedModelAttempt(string version)
    {
        var fixture = new Fixture(profile: version == "full-demo-v1" ? TownReplayBudgetProfile.FullDemo : TownReplayBudgetProfile.FullDemoV2);
        fixture.Complete();
        Assert.That(TownReplay.Run(fixture.Recording.Complete()).Passed, Is.True);
        var noAttempt = fixture.Recording.Complete() with { InitialBudget = fixture.Recording.Complete().InitialBudget! with
            { Attempts = [], TotalAttempts = 0, SlotsHeld = 0, ReservedMicros = 0 } };
        Assert.That(TownReplay.Run(noAttempt).Field, Is.EqualTo("model.budgetAttempt"));
    }

    private static TownReplayPackage AlterDispatches(TownReplayPackage package, Action<JsonNode> change)
    {
        var copy = TownJson.Clone(package);
        for (int i = 0; i < copy.Boundaries.Length; i++)
        {
            var trace = copy.Boundaries[i];
            if (!trace.Command.StartsWith("model_", StringComparison.Ordinal)) continue;
            var node = JsonNode.Parse(trace.InputJson)!; change(node["dispatch"]!);
            string json = node.ToJsonString();
            copy.Boundaries[i] = trace with { InputJson = json, InputFingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(trace.Command + "\n" + json))).ToLowerInvariant() };
        }
        return copy;
    }

    private sealed class Fixture
    {
        public TownWorld World { get; }
        public TownRecording Recording { get; }
        public DecisionRequest Request { get; }
        public object Dispatch { get; }
        public ActionCandidate Candidate { get; }

        public Fixture(bool admitted = true, string? stopReason = null, bool freeze = false, TownReplayBudgetProfile? profile = null)
        {
            var content = TownReplayParentContinuityTests.Content();
            profile ??= TownReplayBudgetProfile.FirstSlice;
            World = new TownWorld(content, budgetId: profile.BudgetId); World.Advance(.001, 0);
            Request = World.GetDecisionRequests().Single();
            var host = freeze ? new TownInputHost(content, World) : null;
            Recording = new TownRecording(content, World, host);
            var attempts = admitted ? new[] { new TownReplayBudgetAttempt(Attempt, "baseline", Request.LogicalRequestId,
                false, "reserved", true, 2_110_000, 0, null, null, null) } : [];
            var budget = new TownReplayBudgetState(profile.BudgetId, "CNY", 5_000_000, 0,
                admitted ? 2_110_000 : 0, attempts.Length, attempts.Length, stopReason, attempts);
            Recording.SetInitialBudget(budget, profile);
            var frozenRequest = new
            {
                identity = new { runId = World.State.BranchId, sessionId = World.SessionId,
                    logicalRequestId = Request.LogicalRequestId, residentId = Request.ResidentId,
                    activityRevision = Request.ActivityRevision, kind = "npc_action", playerSessionId = (string?)null },
                stage = 0, inputJson = TownJson.Serialize(new { messages = new[] { new { role = "user", content = Request.InputJson } } }),
                isRecovery = false, remainingWait = TimeSpan.FromSeconds(60)
            };
            host?.FreezeRequest(Request.LogicalRequestId, TownJson.Serialize(frozenRequest));
            Dispatch = new
            {
                request = frozenRequest,
                attemptId = Attempt, inputFingerprint = "synthetic", reservedAtUtc = DateTimeOffset.UnixEpoch,
                timeout = TimeSpan.FromSeconds(30), budget
            };
            World.MarkDispatched(Request.LogicalRequestId, Attempt);
            Candidate = World.CreateRuleCandidate(Request);
        }

        public void Complete(double transportSeconds = 2, double worldSeconds = 2, double? resultSeconds = null,
            bool worldArrival = true, bool worldArrivalFirst = false, double persistenceDelay = 0)
        {
            string json = TownJson.Serialize(Candidate);
            if (worldArrival && worldArrivalFirst) World.MarkResponseArrived(World.SessionId, Request.LogicalRequestId, Attempt, worldSeconds);
            Recording.RecordExternal("model_response_arrived", new { dispatch = Dispatch,
                responseBody = TownJson.Serialize(new { choices = new[] { new { message = new { content = json } } } }),
                elapsed = TimeSpan.FromSeconds(transportSeconds) });
            if (worldArrival && !worldArrivalFirst) World.MarkResponseArrived(World.SessionId, Request.LogicalRequestId, Attempt, worldSeconds);
            if (persistenceDelay > 0) World.Advance(0, persistenceDelay, true);
            Recording.RecordExternal("model_completion_persisted", new { dispatch = Dispatch,
                dispatchedAtUtc = DateTimeOffset.UnixEpoch, arrivedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(transportSeconds),
                logicalElapsedToArrival = TimeSpan.FromSeconds(transportSeconds), candidateJson = json, failureCode = (string?)null });
            Recording.RecordExternal("model_mailbox_enqueued", new { dispatch = Dispatch });
            Recording.RecordExternal("model_mailbox_entered", new { dispatch = Dispatch });
            World.ReceiveResult(new(World.SessionId, Request.LogicalRequestId, Attempt, Request.ResidentId,
                Candidate, "real_model", null, resultSeconds ?? worldSeconds));
            World.Advance(.1, 0);
        }

        public void CompleteFailure(bool responseArrived, string failure)
        {
            if (responseArrived)
            {
                Recording.RecordExternal("model_response_arrived", new { dispatch = Dispatch, responseBody = "{\"error\":\"fixture\"}", elapsed = TimeSpan.FromSeconds(2) });
                World.MarkResponseArrived(World.SessionId, Request.LogicalRequestId, Attempt, 2);
            }
            Recording.RecordExternal("model_completion_persisted", new { dispatch = Dispatch, dispatchedAtUtc = DateTimeOffset.UnixEpoch,
                arrivedAtUtc = responseArrived ? DateTimeOffset.UnixEpoch.AddSeconds(2) : (DateTimeOffset?)null,
                logicalElapsedToArrival = responseArrived ? TimeSpan.FromSeconds(2) : (TimeSpan?)null,
                candidateJson = (string?)null, failureCode = failure });
            Recording.RecordExternal("model_mailbox_enqueued", new { dispatch = Dispatch });
            Recording.RecordExternal("model_mailbox_entered", new { dispatch = Dispatch });
            World.ReceiveResult(new(World.SessionId, Request.LogicalRequestId, Attempt, Request.ResidentId, null, "real_model", failure, 2));
            World.Advance(.1, 0);
        }
    }
}
