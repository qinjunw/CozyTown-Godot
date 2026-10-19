using CozyTown.Core.Town;
using NUnit.Framework;
using System.Text.Json.Nodes;

namespace CozyTown.Core.Tests;

[TestFixture]
public sealed class TownReplayModelBindingTests
{
    private const string Attempt = "00000000-0000-0000-0000-000000000001";

    [Test]
    public void NpcCompletionFailureCannotRetainTheRecordedSuccessfulWorldResult()
    {
        var fixture = new Fixture(); fixture.Complete();
        var package = AlterInput(fixture.Recording.Complete(), "model_completion_persisted",
            node => node["failureCode"] = "request_timeout");

        var report = TownReplay.Run(package);

        Assert.That(report.Passed, Is.False);
        Assert.That(report.Command, Is.EqualTo("result"));
        Assert.That(report.Field, Is.EqualTo("model.npcError"));
        Assert.That(report.Expected, Is.EqualTo("\"request_timeout\""));
        Assert.That(report.Actual, Is.EqualTo("null"));
    }

    [Test]
    public void NpcResponseWithAnInvalidCandidateSchemaReplaysItsRecordedFailure()
    {
        var fixture = new Fixture();
        fixture.CompleteResponse("{\"action\":\"wait\"}", null, "candidate_schema_invalid");

        var report = TownReplay.Run(fixture.Recording.Complete());

        Assert.That(report.Passed, Is.True, TownJson.Serialize(report));
    }

    [TestCase("request_timeout")]
    [TestCase("candidate_schema_invalid")]
    public void SuccessfulNpcCompletionCannotInventAWorldFailure(string error)
    {
        var fixture = new Fixture();
        fixture.CompleteResponse(TownJson.Serialize(fixture.Candidate), null, error);

        var report = TownReplay.Run(fixture.Recording.Complete());

        Assert.That(report.Passed, Is.False);
        Assert.That(report.Command, Is.EqualTo("result"));
        Assert.That(report.Field, Is.EqualTo("model.npcError"));
    }

    [TestCase("usage_invalid_or_missing")]
    [TestCase("response_late")]
    public void FailedNpcCompletionCanRetainUnusedCandidateJson(string failure)
    {
        var fixture = new Fixture();
        fixture.CompleteResponse(TownJson.Serialize(fixture.Candidate), null, failure, failure);

        var report = TownReplay.Run(fixture.Recording.Complete());

        Assert.That(report.Passed, Is.True, TownJson.Serialize(report));
    }

    [Test]
    public void FailedNpcCompletionCannotPassItsCandidateToTheWorld()
    {
        var fixture = new Fixture();
        fixture.CompleteResponse(TownJson.Serialize(fixture.Candidate), fixture.Candidate, "response_late", "response_late");

        var report = TownReplay.Run(fixture.Recording.Complete());

        Assert.That(report.Passed, Is.False);
        Assert.That(report.Command, Is.EqualTo("result"));
        Assert.That(report.Field, Is.EqualTo("model.worldCandidate"));
    }

    [Test]
    public void NpcCompletionWithoutCandidateJsonReplaysTheMissingCandidateResult()
    {
        var fixture = new Fixture(); fixture.CompleteResponse(null, null, null);

        var report = TownReplay.Run(fixture.Recording.Complete());

        Assert.That(report.Passed, Is.True, TownJson.Serialize(report));
    }

    [TestCase(161, true)]
    [TestCase(200, true)]
    [TestCase(201, false)]
    public void NpcReplayKeepsTheProtocolTextLimitSeparateFromWorldValidation(int length, bool parses)
    {
        var fixture = new Fixture();
        var candidate = fixture.Candidate with { Text = new string('字', length) };
        fixture.CompleteResponse(TownJson.Serialize(candidate), parses ? candidate : null, parses ? null : "candidate_schema_invalid");

        var report = TownReplay.Run(fixture.Recording.Complete());

        Assert.That(report.Passed, Is.True, TownJson.Serialize(report));
    }

    [TestCase(null)]
    [TestCase("invalid\nline")]
    public void NpcResponseWithInvalidDialogueReplaysItsSchemaFailure(string? text)
    {
        var fixture = new Fixture();
        fixture.CompleteResponse(TownJson.Serialize(fixture.Candidate with { Text = text! }), null, "candidate_schema_invalid");

        var report = TownReplay.Run(fixture.Recording.Complete());

        Assert.That(report.Passed, Is.True, TownJson.Serialize(report));
    }

    [Test]
    public void NpcCompletionCannotChangeTheRecordedFailureCode()
    {
        var fixture = new Fixture(); fixture.CompleteFailure(true, "authentication_failed");
        var package = AlterInput(fixture.Recording.Complete(), "model_completion_persisted",
            node => node["failureCode"] = "request_timeout");

        var report = TownReplay.Run(package);

        Assert.That(report.Passed, Is.False);
        Assert.That(report.Command, Is.EqualTo("result"));
        Assert.That(report.Field, Is.EqualTo("model.npcError"));
    }

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

    [TestCase(false, "request_timeout", null)]
    [TestCase(true, "authentication_failed", "{\"error\":\"fixture\"}")]
    [TestCase(true, "provider_http_failure", "<html>502 Bad Gateway</html>")]
    [TestCase(true, "provider_http_failure", "upstream unavailable")]
    public void FailedNpcCompletionRetainsItsBudgetAndIdentityBindings(bool responseArrived, string failure, string? responseBody)
    {
        var fixture = new Fixture(); fixture.CompleteFailure(responseArrived, failure, responseBody);
        var report = TownReplay.Run(fixture.Recording.Complete());
        Assert.That(report.Passed, Is.True, TownJson.Serialize(report));
    }

    [TestCase("<html>502 Bad Gateway</html>", 1, "invalid_recording", "input")]
    [TestCase("{\"error\":\"fixture\"}", 1, "divergence", "model.responseProjectionVersion")]
    [TestCase("{\"projectionVersion\":1}", 2, "divergence", "model.responseProjectionVersion")]
    [TestCase(null, 1, "divergence", "model.responseProjectionVersion")]
    public void VersionedFailedCompletionRequiresItsRecordedProjection(string? responseBody, int version, string code, string field)
    {
        var fixture = new Fixture(); fixture.CompleteFailure(true, "provider_http_failure", responseBody);
        var package = AlterInput(fixture.Recording.Complete(), "model_completion_persisted",
            node => node["responseProjectionVersion"] = version);

        var report = TownReplay.Run(package);

        Assert.That(report.Passed, Is.False);
        Assert.That(report.Command, Is.EqualTo("model_completion_persisted"));
        Assert.That(report.Code, Is.EqualTo(code));
        Assert.That(report.Field, Is.EqualTo(field));
    }

    [Test]
    public void FailedCompletionWithCandidateRequiresItsRecordedJsonResponse()
    {
        var fixture = new Fixture();
        fixture.CompleteResponse(TownJson.Serialize(fixture.Candidate), null, "provider_http_failure", "provider_http_failure");
        var package = AlterInput(fixture.Recording.Complete(), "model_response_arrived",
            node => node["responseBody"] = "<html>502 Bad Gateway</html>");

        var report = TownReplay.Run(package);

        Assert.That(report.Passed, Is.False);
        Assert.That(report.Command, Is.EqualTo("model_completion_persisted"));
        Assert.That(report.Code, Is.EqualTo("invalid_recording"));
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

    private static TownReplayPackage AlterInput(TownReplayPackage package, string command, Action<JsonNode> change)
    {
        var copy = TownJson.Clone(package);
        int index = Array.FindIndex(copy.Boundaries, trace => trace.Command == command);
        var trace = copy.Boundaries[index];
        var node = JsonNode.Parse(trace.InputJson)!; change(node);
        string json = node.ToJsonString();
        copy.Boundaries[index] = trace with { InputJson = json, InputFingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(trace.Command + "\n" + json))).ToLowerInvariant() };
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

        public void CompleteFailure(bool responseArrived, string failure, string? responseBody = "{\"error\":\"fixture\"}")
        {
            if (responseArrived)
            {
                Recording.RecordExternal("model_response_arrived", new { dispatch = Dispatch, responseBody, elapsed = TimeSpan.FromSeconds(2) });
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

        public void CompleteResponse(string? candidateJson, ActionCandidate? candidate, string? error, string? failureCode = null)
        {
            Recording.RecordExternal("model_response_arrived", new { dispatch = Dispatch,
                responseBody = TownJson.Serialize(new { choices = new[] { new { message = new { content = candidateJson } } } }),
                elapsed = TimeSpan.FromSeconds(2) });
            World.MarkResponseArrived(World.SessionId, Request.LogicalRequestId, Attempt, 2);
            Recording.RecordExternal("model_completion_persisted", new { dispatch = Dispatch,
                dispatchedAtUtc = DateTimeOffset.UnixEpoch, arrivedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(2),
                logicalElapsedToArrival = TimeSpan.FromSeconds(2), candidateJson, failureCode });
            Recording.RecordExternal("model_mailbox_enqueued", new { dispatch = Dispatch });
            Recording.RecordExternal("model_mailbox_entered", new { dispatch = Dispatch });
            World.ReceiveResult(new(World.SessionId, Request.LogicalRequestId, Attempt, Request.ResidentId,
                candidate, "real_model", error, 2));
            World.Advance(.1, 0);
        }
    }
}
