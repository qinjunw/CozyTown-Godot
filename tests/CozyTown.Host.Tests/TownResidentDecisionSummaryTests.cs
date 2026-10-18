using CozyTown.Core.Town;
using CozyTown.Game;
using CozyTown.Model;
using NUnit.Framework;

namespace CozyTown.Host.Tests;

[TestFixture]
public sealed class TownResidentDecisionSummaryTests
{
    [Test]
    public void ResidentWithoutDecisionHistoryShowsCurrentActivity()
    {
        var world = World();

        string summary = Format(world);

        Assert.That(summary, Is.EqualTo("当前活动：free\n最近决定：暂无记录"));
    }

    [Test]
    public void ModelInvitationShowsCandidateAndWorldExecutionWithoutClaimingAnExchange()
    {
        var world = World();
        var request = world.GetDecisionRequests().Single(r => r.ResidentId == "mina");
        var candidate = new ActionCandidate("invite", "eli", "mina", null, 0, 0, null, "SECRET_CANDIDATE_TEXT");
        var completion = Completion(world, request, candidate);
        Dispatch(world, request, candidate);
        world.Advance(0, 0);

        string summary = Format(world, completion);

        Assert.Multiple(() =>
        {
            Assert.That(summary, Does.Contain("当前活动：awaiting_response"));
            Assert.That(summary, Does.Contain("来源：真实模型"));
            Assert.That(summary, Does.Contain("候选：invite → eli @mina"));
            Assert.That(summary, Does.Contain("世界：已执行 invite（等待对方回应）"));
            Assert.That(summary, Does.Not.Contain("SECRET_CANDIDATE_TEXT"));
            Assert.That(summary, Does.Not.Contain(world.GetView().BranchId));
            Assert.That(summary, Does.Not.Contain("交换完成"));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CompletedModelCandidateRemainsUnexecutedUntilTheWorldCommits(bool sealedResult)
    {
        var world = World();
        var request = world.GetDecisionRequests().Single(r => r.ResidentId == "mina");
        var candidate = new ActionCandidate("wait", null, null, null, 0, 0, null, "SECRET_PENDING_TEXT");
        if (sealedResult) Dispatch(world, request, candidate);
        else Assert.That(world.MarkDispatched(request.LogicalRequestId, "attempt-mina"), Is.True);

        string summary = Format(world, Completion(world, request, candidate));

        Assert.Multiple(() =>
        {
            Assert.That(summary, Does.Contain("来源：真实模型"));
            Assert.That(summary, Does.Contain("候选：wait"));
            Assert.That(summary, Does.Contain("世界：尚未执行"));
            Assert.That(summary, Does.Not.Contain("已执行"));
            Assert.That(summary, Does.Not.Contain("SECRET_PENDING_TEXT"));
        });
    }

    [Test]
    public void WorldRejectionShowsItsFixedReasonAndSeparatesRuleFallback()
    {
        var world = World();
        var request = world.GetDecisionRequests().Single(r => r.ResidentId == "mina");
        var candidate = new ActionCandidate("wait", "eli", null, null, 0, 0, null, "SECRET_REJECTED_TEXT");
        Dispatch(world, request, candidate);
        world.Advance(0, 0);

        string summary = Format(world, Completion(world, request, candidate));

        Assert.Multiple(() =>
        {
            Assert.That(summary, Does.Contain("世界：已拒绝（unexpected_action_fields）"));
            Assert.That(summary, Does.Contain("回退：规则回退"));
            Assert.That(summary, Does.Not.Contain("世界：已执行"));
            Assert.That(summary, Does.Not.Contain("SECRET_REJECTED_TEXT"));
        });
    }

    [Test]
    public void ProviderFailureIsGenericAndWorldFallbackRemainsVisibleWithoutACompletion()
    {
        var world = World();
        var request = world.GetDecisionRequests().Single(r => r.ResidentId == "mina");
        Dispatch(world, request, null, "PRIVATE_PROVIDER_BODY");
        var failed = Completion(world, request, new("wait", null, null, null, 0, 0, null, "unused"))
            with { CandidateJson = null, FailureCode = "PRIVATE_PROVIDER_BODY" };

        string waiting = Format(world, failed);
        Assert.Multiple(() =>
        {
            Assert.That(waiting, Does.Contain("来源：真实模型失败"));
            Assert.That(waiting, Does.Contain("世界：尚未执行"));
            Assert.That(waiting, Does.Not.Contain("PRIVATE_PROVIDER_BODY"));
        });

        world.Advance(0, 0);
        string fallback = Format(world);
        Assert.Multiple(() =>
        {
            Assert.That(fallback, Does.Contain("来源：规则回退"));
            Assert.That(fallback, Does.Contain("世界：已执行 invite"));
            Assert.That(fallback, Does.Not.Contain("PRIVATE_PROVIDER_BODY"));
        });
    }

    [TestCase("session")]
    [TestCase("player")]
    [TestCase("branch")]
    [TestCase("resident")]
    [TestCase("request")]
    public void UnrelatedCompletionCannotSupplyTheCurrentNpcCandidate(string mismatch)
    {
        var world = World();
        var request = world.GetDecisionRequests().Single(r => r.ResidentId == "mina");
        Assert.That(world.MarkDispatched(request.LogicalRequestId, "attempt-mina"), Is.True);
        var completion = Completion(world, request, new("invite", "eli", "mina", null, 0, 0, null, "PRIVATE_FOREIGN_TEXT"));
        var identity = completion.Dispatch.Request.Identity;
        identity = mismatch switch
        {
            "session" => identity with { SessionId = "retired-session" },
            "player" => identity with { Kind = "player_text" },
            "branch" => identity with { RunId = "other-branch" },
            "resident" => identity with { ResidentId = "eli" },
            _ => identity with { LogicalRequestId = "other-request" }
        };
        completion = completion with { Dispatch = completion.Dispatch with
            { Request = completion.Dispatch.Request with { Identity = identity } } };

        string summary = Format(world, completion);

        Assert.Multiple(() =>
        {
            Assert.That(summary, Does.Contain("来源：等待模型"));
            Assert.That(summary, Does.Contain("候选：不可展示"));
            Assert.That(summary, Does.Contain("世界：尚未执行"));
            Assert.That(summary, Does.Not.Contain("invite"));
            Assert.That(summary, Does.Not.Contain("PRIVATE_FOREIGN_TEXT"));
        });
    }

    [Test]
    public void PlayerEventsAndAnotherBranchDoNotBecomeAnNpcDecision()
    {
        var previous = World();
        var request = previous.GetDecisionRequests().Single(r => r.ResidentId == "mina");
        Dispatch(previous, request, new("invite", "eli", "mina", null, 0, 0, null, "old"));
        previous.Advance(0, 0);
        var world = World();
        var dialogue = new TownDialogueHost(world);
        Assert.That(dialogue.Open("mina", []), Is.True);
        var playerRequest = dialogue.Generate(["dialogue"])!;
        dialogue.CompleteFixed(playerRequest, "{\"text\":\"PRIVATE_PLAYER_TEXT\"}");
        var view = world.GetView();

        string summary = TownResidentDecisionSummary.Format(view.Residents.Single(r => r.Id == "mina"),
            view.BranchId, world.SessionId, world.Events.Concat(previous.Events).ToArray(), []);

        Assert.That(summary, Is.EqualTo("当前活动：free\n最近决定：暂无记录"));
    }

    [TestCase("PRIVATE_MALFORMED_PROVIDER_JSON")]
    [TestCase("{\"action\":\"PRIVATE_UNKNOWN_ACTION\",\"targetId\":null,\"placeId\":null,\"resourceId\":null,\"quantity\":0,\"price\":0,\"accept\":null,\"text\":\"PRIVATE_TEXT\"}")]
    public void UnsafeCandidateBodiesAreNotDisplayed(string candidateJson)
    {
        var world = World();
        var request = world.GetDecisionRequests().Single(r => r.ResidentId == "mina");
        Assert.That(world.MarkDispatched(request.LogicalRequestId, "attempt-mina"), Is.True);
        var completion = Completion(world, request, new("wait", null, null, null, 0, 0, null, "unused"))
            with { CandidateJson = candidateJson };

        string summary = Format(world, completion);

        Assert.Multiple(() =>
        {
            Assert.That(summary, Does.Contain("候选：不可展示"));
            Assert.That(summary, Does.Contain("世界：尚未执行"));
            Assert.That(summary, Does.Not.Contain("PRIVATE_"));
            Assert.That(summary, Does.Not.Contain("{\""));
        });
    }

    [Test]
    public void ANewPendingRequestDoesNotReuseThePreviousExecutionOrCompletion()
    {
        var world = World();
        var requests = world.GetDecisionRequests();
        var previous = requests.Single(r => r.ResidentId == "mina");
        var wait = new ActionCandidate("wait", null, null, null, 0, 0, null, "wait");
        Dispatch(world, previous, wait);
        world.Advance(0, 0);
        Dispatch(world, requests.Single(r => r.ResidentId == "eli"), new("approach", "mina", null, null, 0, 0, null, "approach"));
        world.Advance(0, 31);
        var next = world.GetDecisionRequests().Single(r => r.ResidentId == "mina");
        Assert.That(next.LogicalRequestId, Is.Not.EqualTo(previous.LogicalRequestId));

        string summary = Format(world, Completion(world, previous, wait));

        Assert.Multiple(() =>
        {
            Assert.That(summary, Does.Contain("来源：等待模型"));
            Assert.That(summary, Does.Contain("世界：尚未执行"));
            Assert.That(summary, Does.Not.Contain("世界：已执行"));
        });
    }

    [TestCase(null, "candidate_executed")]
    [TestCase("eli", "candidate_rejected")]
    public void EvictedWorldOutcomeIsReportedAsInsufficientRecordsInsteadOfUnexecuted(string? target, string outcomeKind)
    {
        var world = World();
        var request = world.GetDecisionRequests().Single(r => r.ResidentId == "mina");
        var candidate = new ActionCandidate("wait", target, null, null, 0, 0, null, "wait");
        var completion = Completion(world, request, candidate);
        Dispatch(world, request, candidate);
        world.Advance(0, 0);
        for (int i = 0; i < 121; i++)
            Assert.That(world.SetPlayerPosition(new(160 + i % 2, 160)), Is.True);
        var view = world.GetView();
        var resident = view.Residents.Single(r => r.Id == "mina");
        var recentEvents = world.Events.Where(e => e.Kind is not ("time_input" or "space_query")).TakeLast(120).ToArray();
        Assert.That(resident.PendingRequestId, Is.Null);
        Assert.That(world.Events.Any(e => e.LogicalRequestId == request.LogicalRequestId && e.Kind == outcomeKind), Is.True);
        Assert.That(recentEvents.Any(e => e.LogicalRequestId == request.LogicalRequestId), Is.False);

        string summary = TownResidentDecisionSummary.Format(resident, view.BranchId, world.SessionId, recentEvents, [completion]);

        Assert.That(summary, Does.Contain("世界：记录不足").And.Not.Contain("世界：尚未执行"));
    }

    [Test]
    public void ExecutedSaleProposalStillAwaitsTheBuyerInsteadOfClaimingACompletedExchange()
    {
        var world = World();
        Dispatch(world, world.GetDecisionRequests().Single(r => r.ResidentId == "mina"),
            new("invite", "eli", "mina", null, 0, 0, null, "invite"));
        world.Advance(0, 0);
        Dispatch(world, world.GetDecisionRequests().Single(r => r.ResidentId == "eli"),
            new("respond", "mina", null, null, 0, 0, true, "accept"));
        world.Advance(8, 0);
        Dispatch(world, world.GetDecisionRequests().Single(r => r.ResidentId == "mina"),
            new("propose_exchange", "eli", null, "apple", 1, 2, null, "offer"));
        world.Advance(0, 0);

        string summary = Format(world);

        Assert.Multiple(() =>
        {
            Assert.That(summary, Does.Contain("候选：propose_exchange → eli apple ×1 / 总价2"));
            Assert.That(summary, Does.Contain("世界：已执行 propose_exchange（等待对方接受报价）"));
            Assert.That(summary, Does.Not.Contain("交换完成"));
        });
    }

    private static TownWorld World()
    {
        string[] ids = ["mina", "eli", "ren", "sora"];
        var places = ids.Select((id, i) => new PlaceDefinition(id, id, new(80 + i * 16, 96), "outside")).ToArray();
        var content = TownContent.Create(new(256, 192, places, [], [new("outside", new(0, 0, 256, 192))]),
            new(ids.Select(id => new ResidentDefinition(id, id, id, id, "apple", new() { ["apple"] = 3 }, 5,
                [new("free", 0, 1440, id, "free", true)])).ToArray(), [new("apple", "Apple")]));
        var world = new TownWorld(content);
        world.Advance(.001, 0);
        return world;
    }

    private static string Format(TownWorld world, params ModelCompletion[] completions)
    {
        var view = world.GetView();
        return TownResidentDecisionSummary.Format(view.Residents.Single(r => r.Id == "mina"),
            view.BranchId, world.SessionId, world.Events, completions);
    }

    private static void Dispatch(TownWorld world, DecisionRequest request, ActionCandidate? candidate, string? error = null)
    {
        Assert.That(world.MarkDispatched(request.LogicalRequestId, "attempt-" + request.ResidentId), Is.True);
        Assert.That(world.ReceiveResult(new(world.SessionId, request.LogicalRequestId, "attempt-" + request.ResidentId,
            request.ResidentId, candidate, "real_model", error)), Is.True);
    }

    private static ModelCompletion Completion(TownWorld world, DecisionRequest request, ActionCandidate candidate)
    {
        var budget = new BudgetSnapshot(TownWorld.DefaultBudgetId, "CNY", 5000000, 0, 0, 0, 0, null, []);
        var frozen = new FrozenModelRequest(new(world.GetView().BranchId, world.SessionId, request.LogicalRequestId,
            request.ResidentId, request.ActivityRevision), ModelStage.Baseline, TownModelProtocol.BuildRequest(request),
            false, TimeSpan.FromSeconds(60));
        var dispatch = new ModelDispatch(frozen, "attempt-" + request.ResidentId, request.InputFingerprint,
            DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(30), budget);
        return new(dispatch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(1), TimeSpan.FromSeconds(1),
            200, "provider", "deepseek-flash", "fixed", "stop", TownJson.Serialize(candidate), new(100, 10, 110, 0, 100),
            null, true, true, false, 280);
    }
}
