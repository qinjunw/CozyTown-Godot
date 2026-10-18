using CozyTown.Core.Town;
using NUnit.Framework;

namespace CozyTown.Core.Tests;

public sealed class TownManualPlayReplayTests
{
    private const string Attempt = "00000000-0000-0000-0000-000000000001";

    [TestCase("legacy")]
    [TestCase("experiment")]
    public void ManualBudgetRejectsACompletionWithAnotherExecutionPurpose(string purpose)
    {
        var report = TownReplay.Run(Record(purpose));

        Assert.That(report.Passed, Is.False);
        Assert.That(report.Field, Is.EqualTo("model.executionPurpose"));
    }

    [Test]
    public void ManualBudgetRejectsAnotherPurposeAtTheArrivalBeforeCompletion()
    {
        var package = Record(arrivalPurpose: "experiment");
        int arrival = Array.FindIndex(package.Boundaries, boundary => boundary.Command == "model_response_arrived");
        package = package with { Boundaries = package.Boundaries.Take(arrival + 1).ToArray(), BoundaryCount = arrival + 1 };

        var report = TownReplay.Run(package);

        Assert.That(report.Passed, Is.False);
        Assert.That(report.Command, Is.EqualTo("model_response_arrived"));
        Assert.That(report.Field, Is.EqualTo("model.executionPurpose"));
    }

    [TestCase(null)]
    [TestCase("usage_invalid_or_missing")]
    [TestCase("model_not_registered")]
    public void ManualArrivalProjectionReplaysItsCandidateOrStoppedUnknownBudget(string? failure)
    {
        var report = TownReplay.Run(Record(failure: failure));

        Assert.That(report.Passed, Is.True, TownJson.Serialize(report));
    }

    [TestCase("identity")]
    [TestCase("limits")]
    [TestCase("stage")]
    public void ManualRecordingRejectsAnotherBudgetIdentityStageOrLimits(string change)
    {
        var package = Record();
        package = change switch
        {
            "identity" => package with { InitialBudget = package.InitialBudget! with { BudgetId = "cozytown-godot-full-demo-20261011-v2" } },
            "limits" => package with { BudgetProfile = package.BudgetProfile! with { TotalLimit = 97 } },
            _ => package with { InitialBudget = package.InitialBudget! with { Attempts = [package.InitialBudget.Attempts[0] with { Stage = "baseline" }] } }
        };

        var report = TownReplay.Run(package);

        Assert.That(report.Passed, Is.False);
        Assert.That(report.Field, Is.EqualTo(change switch { "identity" => "budget.budgetId", "limits" => "budget.profile", _ => "budget.attempts[0].stage" }));
    }

    private static TownReplayPackage Record(string purpose = "manual", string? failure = null, string arrivalPurpose = "manual")
    {
        var content = TownReplayParentContinuityTests.Content();
        var profile = TownReplayBudgetProfile.ManualPlay;
        var world = new TownWorld(content, budgetId: profile.BudgetId);
        world.Advance(.001, 0);
        var request = world.GetDecisionRequests().Single();
        var recording = new TownRecording(content, world);
        var reserved = new TownReplayBudgetAttempt(Attempt, "manual", request.LogicalRequestId, false,
            "reserved", true, 2_110_000, 0, null, null, null);
        var budget = new TownReplayBudgetState(profile.BudgetId, "CNY", 5_000_000, 0, 2_110_000, 1, 1, null, [reserved]);
        recording.SetInitialBudget(budget, profile);
        var dispatch = new
        {
            request = new
            {
                identity = new { runId = world.State.BranchId, sessionId = world.SessionId,
                    logicalRequestId = request.LogicalRequestId, residentId = request.ResidentId,
                    activityRevision = request.ActivityRevision, kind = "npc_action", playerSessionId = (string?)null },
                stage = 3, inputJson = request.InputJson, isRecovery = false, remainingWait = TimeSpan.FromSeconds(60)
            },
            attemptId = Attempt, inputFingerprint = "synthetic", reservedAtUtc = DateTimeOffset.UnixEpoch,
            timeout = TimeSpan.FromSeconds(30), budget
        };
        world.MarkDispatched(request.LogicalRequestId, Attempt);
        var candidate = failure is null ? world.CreateRuleCandidate(request) : null;
        string? candidateJson = candidate is null ? null : TownJson.Serialize(candidate);
        var usage = failure is null ? new { promptTokens = 100, completionTokens = 10, totalTokens = 110,
            cacheHitTokens = 70, cacheMissTokens = 30 } : null;
        string returnedModel = failure == "model_not_registered" ? "unregistered-model" : "deepseek-flash";
        var projection = new
        {
            projectionVersion = 1, httpStatus = 200, validationFailure = failure, generationEnded = true,
            id = "synthetic-response", model = returnedModel, system_fingerprint = "different-safe-fingerprint",
            choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = candidateJson } } },
            usage = failure is null ? new { prompt_tokens = 100, completion_tokens = 10, total_tokens = 110,
                prompt_cache_hit_tokens = 70, prompt_cache_miss_tokens = 30 } : null,
            executionPurpose = arrivalPurpose, expectedSystemFingerprint = "registered-fingerprint"
        };
        recording.RecordExternal("model_response_arrived", new { dispatch, responseBody = TownJson.Serialize(projection), elapsed = TimeSpan.FromSeconds(2) });
        world.MarkResponseArrived(world.SessionId, request.LogicalRequestId, Attempt, 2);
        var after = failure is null
            ? budget with { SettledMicros = 280, ReservedMicros = 0, SlotsHeld = 0,
                Attempts = [reserved with { Status = "settled", SlotHeld = false, ReservedCostMicros = 0,
                    SettledCostMicros = 280, PromptTokens = 100, CompletionTokens = 10 }] }
            : budget with { SlotsHeld = 0, StopReason = failure,
                Attempts = [reserved with { Status = "unknown", SlotHeld = false, FailureCode = failure }] };
        if (failure is null)
            recording.RecordExternal("budget_settle", new { attemptId = Attempt, promptTokens = 100, completionTokens = 10, after });
        else
            recording.RecordExternal("budget_unknown", new { attemptId = Attempt, reason = failure, generationEnded = true, stopFurther = true, after });
        recording.RecordExternal("model_completion_persisted", new
        {
            dispatch, dispatchedAtUtc = DateTimeOffset.UnixEpoch, arrivedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(2),
            logicalElapsedToArrival = TimeSpan.FromSeconds(2), httpStatus = 200, generationEnded = true,
            providerRequestId = "synthetic-response", returnedModel, systemFingerprint = "different-safe-fingerprint",
            finishReason = "stop", candidateJson, usage, failureCode = failure, executionPurpose = purpose,
            expectedSystemFingerprint = "registered-fingerprint", providerWarningCode = purpose == "manual" && failure is null ? "fingerprint_changed" : null,
            responseProjectionVersion = 1
        });
        recording.RecordExternal("model_mailbox_enqueued", new { dispatch });
        recording.RecordExternal("model_mailbox_entered", new { dispatch });
        world.ReceiveResult(new(world.SessionId, request.LogicalRequestId, Attempt, request.ResidentId, candidate, "real_model", failure, 2));
        world.Advance(.1, 0);
        return recording.Complete();
    }
}
