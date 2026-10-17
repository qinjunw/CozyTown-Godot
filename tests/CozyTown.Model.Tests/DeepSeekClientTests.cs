using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace CozyTown.Model.Tests;

[TestFixture]
public sealed class DeepSeekClientTests
{
    [Test]
    public async Task ManualFingerprintChangeIsRecordedAndSettledWithoutStoppingTheSharedBudget()
    {
        using var fixture = new ModelFixture();
        string path = Path.Combine(fixture.Root, "manual.json");
        PersistentBudgetLedger.InitializeNew(path, "full-demo-v2");
        var journal = new MemoryJournal();
        using var client = new DeepSeekClient(new(fixture.CredentialPath, path,
            BudgetProfileVersion: "full-demo-v2", ExecutionPurpose: "manual"), journal, new FakeHandler("fingerprint-mismatch"));
        var result = await client.SendAsync(Request() with { Stage = ModelStage.Manual });
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.ProviderWarningCode, Is.EqualTo("fingerprint_changed"));
            Assert.That(result.SystemFingerprint, Is.EqualTo("different"));
            Assert.That(result.ExpectedSystemFingerprint, Is.EqualTo("aeb56401ca74e127821c4f9126dcb669"));
            Assert.That(result.ExecutionPurpose, Is.EqualTo("manual"));
            Assert.That(client.Budget.StopReason, Is.Null);
            Assert.That(client.Budget.SettledMicros, Is.EqualTo(280));
            Assert.That(client.Budget.ReservedMicros, Is.Zero);
            Assert.That(journal.Completions.Single(), Is.EqualTo(result));
        });
    }

    [TestCase("legacy")]
    [TestCase("experiment")]
    public async Task ExperimentAndLegacyKeepStrictFingerprintRegistration(string purpose)
    {
        using var fixture = new ModelFixture(); string path = Path.Combine(fixture.Root, "strict.json");
        PersistentBudgetLedger.InitializeNew(path, "full-demo-v2");
        using var client = new DeepSeekClient(new(fixture.CredentialPath, path,
            BudgetProfileVersion: "full-demo-v2", ExecutionPurpose: purpose), new MemoryJournal(), new FakeHandler("fingerprint-mismatch"));
        var result = await client.SendAsync(Request());
        Assert.That(result.FailureCode, Is.EqualTo("fingerprint_not_registered"));
        Assert.That(client.Budget.StopReason, Is.EqualTo("fingerprint_not_registered"));
        Assert.That(client.Budget.ReservedMicros, Is.EqualTo(2110000));
        Assert.That(client.Budget.SlotsHeld, Is.Zero);
        Assert.That(result.ProviderWarningCode, Is.Null);
    }

    [Test]
    public void ManualProviderPolicyCannotBeUsedForAnExperimentStage()
    {
        using var fixture = new ModelFixture(); string path = Path.Combine(fixture.Root, "manual.json");
        PersistentBudgetLedger.InitializeNew(path, "full-demo-v2"); var handler = new FakeHandler("success");
        using var client = new DeepSeekClient(new(fixture.CredentialPath, path,
            BudgetProfileVersion: "full-demo-v2", ExecutionPurpose: "manual"), new MemoryJournal(), handler);
        Assert.That(Assert.ThrowsAsync<ModelException>(async () => await client.SendAsync(Request()))!.Code, Is.EqualTo("execution_stage_mismatch"));
        Assert.That(client.Budget.TotalAttempts, Is.Zero);
        Assert.That(handler.TotalCalls, Is.Zero);
    }

    [TestCase("model-mismatch", "model_not_registered")]
    [TestCase("no-usage", "usage_invalid_or_missing")]
    [TestCase("invalid-cache", "usage_invalid_or_missing")]
    [TestCase("no-fingerprint", "fingerprint_not_registered")]
    public async Task ManualPolicyStillRejectsUnverifiedModelUsageOrMissingFingerprint(string mode, string code)
    {
        using var fixture = new ModelFixture(); string path = Path.Combine(fixture.Root, "manual.json");
        PersistentBudgetLedger.InitializeNew(path, "full-demo-v2");
        using var client = new DeepSeekClient(new(fixture.CredentialPath, path,
            BudgetProfileVersion: "full-demo-v2", ExecutionPurpose: "manual"), new MemoryJournal(), new FakeHandler(mode));
        var result = await client.SendAsync(Request() with { Stage = ModelStage.Manual });
        Assert.That(result.FailureCode, Is.EqualTo(code));
        Assert.That(result.ProviderWarningCode, Is.Null);
        Assert.That(client.Budget.StopReason, Is.EqualTo(code));
        Assert.That(client.Budget.ReservedMicros, Is.EqualTo(2110000));
    }

    [Test]
    public void StoppedBudgetReportsItsOriginalCauseBeforeContactingProvider()
    {
        using var fixture = new ModelFixture();
        PersistentBudgetLedger.OpenExisting(fixture.LedgerPath).Stop("fingerprint_not_registered");
        var handler = new FakeHandler("success");
        using var client = Client(fixture, new MemoryJournal(), handler);
        var failure = Assert.ThrowsAsync<ModelException>(async () => await client.SendAsync(Request()));
        Assert.That(failure!.Code, Is.EqualTo("ledger_stopped"));
        Assert.That(failure.StopReason, Is.EqualTo("fingerprint_not_registered"));
        Assert.That(handler.TotalCalls, Is.Zero);
        Assert.That(client.Budget.TotalAttempts, Is.EqualTo(4));
    }

    [TestCase("success", null)]
    [TestCase("http-502", "provider_http_failure")]
    [TestCase("duplicate-response-field", "duplicate_json_property")]
    [TestCase("duplicate-unused-field", "duplicate_json_property")]
    [TestCase("bad-json", "invalid_json")]
    [TestCase("invalid-secret-finish", "generation_terminal_unverified")]
    public async Task DurableArrivalContainsOnlyReplayProjectionAndRetainsValidationFailure(string mode, string? failure)
    {
        using var fixture = new ModelFixture(); var journal = new MemoryJournal();
        using var client = Client(fixture, journal, new FakeHandler(mode));
        var result = await client.SendAsync(Request());
        string persisted = JsonSerializer.Serialize(new { journal.Arrivals, journal.Completions });
        Assert.That(persisted, Does.Not.Contain("discarded-reasoning").And.Not.Contain("unused-provider-extra")
            .And.Not.Contain("discarded-http-body").And.Not.Contain(ModelFixture.FakeKey));
        using var arrival = JsonDocument.Parse(journal.Arrivals.Single()!);
        Assert.That(arrival.RootElement.GetProperty("projectionVersion").GetInt32(), Is.EqualTo(1));
        Assert.That(arrival.RootElement.GetProperty("validationFailure").GetString(), Is.EqualTo(failure));
        Assert.That(result.FailureCode, Is.EqualTo(failure));
        if (failure is null)
            Assert.That(arrival.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString(), Is.EqualTo(result.CandidateJson));
    }

    [TestCase("full-demo-v1", "full-demo-v2")]
    [TestCase("full-demo-v2", "full-demo-v1")]
    public void FullDemoClientRejectsTheOtherAuthorizedLedgerBeforeReadingCredentials(string ledgerProfile, string clientProfile)
    {
        using var fixture = new ModelFixture(); var handler = new FakeHandler("success");
        string path = Path.Combine(fixture.Root, "full-demo.json");
        var ledger = PersistentBudgetLedger.InitializeNew(path, ledgerProfile);
        File.Delete(fixture.CredentialPath);
        BudgetLedgerTests.AssertCode("ledger_lock_invalid", () => new DeepSeekClient(
            new(fixture.CredentialPath, path, BudgetProfileVersion: clientProfile), new MemoryJournal(), handler));
        Assert.That(handler.TotalCalls, Is.Zero);
        Assert.That(ledger.Snapshot().TotalAttempts, Is.Zero);
    }

    [TestCase("full-demo-v1", "cozytown-godot-full-demo-20261010-v1")]
    [TestCase("full-demo-v2", "cozytown-godot-full-demo-20261011-v2")]
    public async Task FullDemoClientExplicitlySelectsItsLedgerAndAccountsPlayerAndManualStages(string profileVersion, string budgetId)
    {
        using var fixture = new ModelFixture(); var handler = new FakeHandler("success");
        string path = Path.Combine(fixture.Root, "full-demo.json");
        PersistentBudgetLedger.InitializeNew(path, profileVersion);
        using var client = new DeepSeekClient(new(fixture.CredentialPath, path,
            BudgetProfileVersion: profileVersion), new MemoryJournal(), handler);
        var npc = Request("manual-npc") with { Stage = ModelStage.Manual };
        var player = Request("player-text") with { Stage = ModelStage.PlayerDialogue,
            Identity = new("run", "session", "player-text", "mina", 7, "player_text", "player-session") };
        Assert.That((await client.SendAsync(npc)).Succeeded, Is.True);
        Assert.That((await client.SendAsync(player)).Succeeded, Is.True);
        Assert.That(client.Budget.BudgetId, Is.EqualTo(budgetId));
        Assert.That(client.Budget.SettledMicros, Is.EqualTo(560));
        Assert.That(client.Budget.Attempts.Select(a => a.Stage), Is.EqualTo(new[] { "manual", "player_dialogue" }));
    }

    [TestCase(ModelStage.Manual)]
    [TestCase(ModelStage.PlayerDialogue)]
    [TestCase((ModelStage)99)]
    public void UnregisteredStageCannotReachTheAccountEndpoint(ModelStage stage)
    {
        using var fixture = new ModelFixture(); var handler = new FakeHandler("success");
        using var client = Client(fixture, new MemoryJournal(), handler);
        Assert.That(Assert.ThrowsAsync<ModelException>(async () => await client.SendAsync(Request() with { Stage = stage }))!.Code,
            Is.EqualTo("stage_invalid"));
        Assert.That(handler.TotalCalls, Is.Zero);
        Assert.That(client.Budget.TotalAttempts, Is.EqualTo(4));
    }

    [Test]
    public void WrongClientProfileFailsBeforeCredentialsOrHttpAreUsed()
    {
        using var fixture = new ModelFixture(); var handler = new FakeHandler("success");
        string path = Path.Combine(fixture.Root, "full-demo.json");
        PersistentBudgetLedger.InitializeNew(path, "full-demo-v1");
        File.Delete(fixture.CredentialPath);
        BudgetLedgerTests.AssertCode("ledger_lock_invalid", () => new DeepSeekClient(
            new(fixture.CredentialPath, fixture.LedgerPath, BudgetProfileVersion: "full-demo-v1"), new MemoryJournal(), handler));
        BudgetLedgerTests.AssertCode("ledger_lock_invalid", () => new DeepSeekClient(
            new(fixture.CredentialPath, path), new MemoryJournal(), handler));
        BudgetLedgerTests.AssertCode("budget_profile_not_registered", () => new DeepSeekClient(
            new(fixture.CredentialPath, path, BudgetProfileVersion: "unregistered"), new MemoryJournal(), handler));
        Assert.That(handler.TotalCalls, Is.Zero);
        Assert.That(PersistentBudgetLedger.OpenExisting(path, "full-demo-v1").Snapshot().TotalAttempts, Is.Zero);
        Assert.That(PersistentBudgetLedger.OpenExisting(fixture.LedgerPath).Snapshot().TotalAttempts, Is.EqualTo(4));
    }

    [Test]
    public async Task PlayerTextUsesSameAccountedTransportAndRequiresItsOwnSessionIdentity()
    {
        using var fixture = new ModelFixture(); var journal = new MemoryJournal(); var handler = new FakeHandler("success");
        using var client = Client(fixture, journal, handler);
        var npcShape = Request();
        var player = npcShape with { Identity = npcShape.Identity with { Kind = "player_text", PlayerSessionId = "player-session" } };
        var completion = await client.SendAsync(player);
        Assert.That(completion.Succeeded, Is.True);
        Assert.That(completion.BudgetSettled, Is.True);
        Assert.That(journal.Requests.Single().Request.Identity.Kind, Is.EqualTo("player_text"));
        Assert.That(journal.Requests.Single().Request.Identity.PlayerSessionId, Is.EqualTo("player-session"));
        Assert.That(client.Budget.TotalAttempts, Is.EqualTo(5));
        Assert.That(client.Budget.SettledMicros, Is.EqualTo(2492));
        var invalid = player with { Identity = player.Identity with { LogicalRequestId = "unbound-player", PlayerSessionId = null } };
        Assert.That(Assert.ThrowsAsync<ModelException>(async () => await client.SendAsync(invalid))!.Code, Is.EqualTo("request_identity_invalid"));
        Assert.That(client.Budget.TotalAttempts, Is.EqualTo(5));
        Assert.That(handler.ChatCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task SuccessfulCompletionIsAccountedBeforeHostConsumesIt()
    {
        using var fixture = new ModelFixture();
        var handler = new FakeHandler("success");
        var journal = new MemoryJournal();
        using var client = Client(fixture, journal, handler);
        var request = Request();
        var result = await client.SendAsync(request);
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.Dispatch.Request, Is.SameAs(request));
            Assert.That(result.CandidateJson, Is.EqualTo("{\"operation\":\"wait\"}"));
            Assert.That(result.BudgetSettled, Is.True);
            Assert.That(result.SlotHeld, Is.False);
            Assert.That(result.Usage, Is.EqualTo(new ModelUsage(100, 10, 110, 70, 30)));
            Assert.That(result.ConservativeCostMicros, Is.EqualTo(280));
            Assert.That(client.Budget.SettledMicros, Is.EqualTo(2492));
            Assert.That(handler.ChatCalls, Is.EqualTo(1));
            Assert.That(journal.Requests.Single().Budget.TotalAttempts, Is.EqualTo(5));
            Assert.That(journal.Completions.Single(), Is.EqualTo(result));
            Assert.That(JsonSerializer.Serialize(result), Does.Not.Contain(ModelFixture.FakeKey).And.Not.Contain("discarded-reasoning"));
        });
    }

    [TestCase("no-usage", "usage_invalid_or_missing", false, false, true)]
    [TestCase("invalid-cache", "usage_invalid_or_missing", false, false, true)]
    [TestCase("model-mismatch", "model_not_registered", false, false, true)]
    [TestCase("fingerprint-mismatch", "fingerprint_not_registered", false, false, true)]
    [TestCase("no-fingerprint", "fingerprint_not_registered", false, false, true)]
    [TestCase("length", "generation_length", true, false, false)]
    [TestCase("aborted", "generation_aborted", true, false, false)]
    [TestCase("empty", "candidate_empty", true, false, false)]
    [TestCase("bad-json", "invalid_json", true, false, false)]
    [TestCase("http-502", "provider_http_failure", false, true, false)]
    [TestCase("http-401", "authentication_failed", false, true, true)]
    [TestCase("http-429", "rate_limited", false, true, false)]
    [TestCase("duplicate-response-field", "duplicate_json_property", false, true, true)]
    [TestCase("secret-echo", "response_secret_echo", false, false, true)]
    [TestCase("invalid-secret-candidate", "invalid_json", true, false, false)]
    [TestCase("invalid-secret-finish", "generation_terminal_unverified", false, true, true)]
    public async Task FailuresRetainTheCorrectFeeAndPhysicalSlot(string mode, string failure,
        bool settled, bool slot, bool stopped)
    {
        using var fixture = new ModelFixture();
        var journal = new MemoryJournal();
        var handler = new FakeHandler(mode);
        using var client = Client(fixture, journal, handler);
        var result = await client.SendAsync(Request());
        Assert.Multiple(() =>
        {
            Assert.That(result.FailureCode, Is.EqualTo(failure));
            Assert.That(result.BudgetSettled, Is.EqualTo(settled));
            Assert.That(result.SlotHeld, Is.EqualTo(slot));
            Assert.That(client.Budget.StopReason != null, Is.EqualTo(stopped));
            Assert.That(client.Budget.ReservedMicros, Is.EqualTo(settled ? 0 : 2_110_000));
            Assert.That(handler.ChatCalls, Is.EqualTo(1));
            Assert.That(JsonSerializer.Serialize(result), Does.Not.Contain(ModelFixture.FakeKey));
        });
    }

    [Test]
    public async Task CancellationKeepsUnknownReservationAndAllowsOtherLogicalRequest()
    {
        using var fixture = new ModelFixture();
        var handler = new FakeHandler("wait");
        using var client = Client(fixture, new MemoryJournal(), handler);
        using var cancellation = new CancellationTokenSource();
        var pending = client.SendAsync(Request(), cancellation.Token);
        await handler.ChatStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = await pending;
        Assert.That(result.FailureCode, Is.EqualTo("transport_timeout_or_cancelled"));
        Assert.That(result.SlotHeld, Is.True);
        Assert.That(client.Budget.StopReason, Is.Null);
        var existing = PersistentBudgetLedger.OpenExisting(fixture.LedgerPath);
        existing.Reserve(ModelStage.Baseline, "other-request");
        Assert.That(existing.Snapshot().SlotsHeld, Is.EqualTo(2));
    }

    [Test]
    public async Task TransportDeadlineUsesRemainingLogicalWait()
    {
        using var fixture = new ModelFixture();
        var handler = new FakeHandler("wait");
        using var client = Client(fixture, new MemoryJournal(), handler);
        await client.VerifyAccountAsync();
        var result = await client.SendAsync(Request() with { RemainingWait = TimeSpan.FromMilliseconds(150) });
        Assert.That(result.FailureCode, Is.EqualTo("transport_timeout_or_cancelled"));
        Assert.That(result.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3)));
        Assert.That(result.Dispatch.Timeout, Is.LessThanOrEqualTo(TimeSpan.FromMilliseconds(150)));
    }

    [Test]
    public async Task RequestJournalFailurePreventsPaidSendAndStopsLedger()
    {
        using var fixture = new ModelFixture();
        var handler = new FakeHandler("success");
        var journal = new MemoryJournal { FailRequest = true };
        using var client = Client(fixture, journal, handler);
        var result = await client.SendAsync(Request());
        Assert.That(handler.ChatCalls, Is.Zero);
        Assert.That(result.DispatchedAtUtc, Is.Null);
        Assert.That(result.FailureCode, Is.EqualTo("request_journal_failed"));
        Assert.That(client.Budget.SettledMicros, Is.EqualTo(2212));
        Assert.That(client.Budget.SlotsHeld, Is.Zero);
        Assert.That(client.Budget.StopReason, Is.EqualTo("request_journal_failed"));
    }

    [Test]
    public async Task CompletionJournalFailurePreservesChargeAndStopsFutureSends()
    {
        using var fixture = new ModelFixture();
        using var client = Client(fixture, new MemoryJournal { FailCompletion = true }, new FakeHandler("success"));
        var result = await client.SendAsync(Request());
        Assert.That(result.FailureCode, Is.EqualTo("completion_journal_failed"));
        Assert.That(client.Budget.SettledMicros, Is.EqualTo(2492));
        Assert.That(client.Budget.StopReason, Is.EqualTo("completion_journal_failed"));
    }

    [TestCase("host_request_retired_before_send")]
    [TestCase("host_request_terminal_before_send")]
    [TestCase("host_unavailable_before_send")]
    [TestCase("logical_wait_expired")]
    public async Task HostAdmissionRejectionReleasesUnsentReservationWithoutStoppingBudget(string code)
    {
        using var fixture = new ModelFixture();
        var handler = new FakeHandler("success");
        using var client = Client(fixture, new MemoryJournal { RejectRequestCode = code }, handler);
        var result = await client.SendAsync(Request());
        Assert.That(result.FailureCode, Is.EqualTo(code));
        Assert.That(result.DispatchedAtUtc, Is.Null);
        Assert.That(result.BudgetSettled, Is.True);
        Assert.That(result.ConservativeCostMicros, Is.Zero);
        Assert.That(handler.ChatCalls, Is.Zero);
        Assert.That(client.Budget.SettledMicros, Is.EqualTo(2212));
        Assert.That(client.Budget.SlotsHeld, Is.Zero);
        Assert.That(client.Budget.StopReason, Is.Null);
    }

    [Test]
    public async Task UnregisteredJournalExceptionCodeIsNotExported()
    {
        using var fixture = new ModelFixture();
        using var client = Client(fixture, new MemoryJournal { RejectRequestCode = ModelFixture.FakeKey }, new FakeHandler("success"));
        var result = await client.SendAsync(Request());
        Assert.That(result.FailureCode, Is.EqualTo("request_journal_failed"));
        Assert.That(JsonSerializer.Serialize(result), Does.Not.Contain(ModelFixture.FakeKey));
        Assert.That(client.Budget.StopReason, Is.EqualTo("request_journal_failed"));
    }

    [Test]
    public async Task SlowCompletionJournalDoesNotChangeTheRecordedArrivalOrExpireTimelyResponse()
    {
        using var fixture = new ModelFixture();
        var journalReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseJournal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var journal = new MemoryJournal
        {
            OnCompletion = _ => { journalReached.SetResult(); return releaseJournal.Task; }
        };
        using var client = Client(fixture, journal, new FakeHandler("success"));
        TimeSpan? notifiedArrival = null;
        bool notificationPrecededCompletionJournal = false;
        client.ResponseArrived += (_, elapsed) =>
        {
            notifiedArrival = elapsed;
            notificationPrecededCompletionJournal = journal.Completions.Count == 0;
        };
        await client.VerifyAccountAsync();
        var pending = client.SendAsync(Request() with { RemainingWait = TimeSpan.FromSeconds(2) });
        await journalReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var recordedArrival = journal.Completions.Single().LogicalElapsedToArrival;
        try
        {
            Assert.That(recordedArrival, Is.Not.Null);
            Assert.That(recordedArrival, Is.LessThan(TimeSpan.FromSeconds(2)));
            Assert.That(notificationPrecededCompletionJournal, Is.True);
            Assert.That(notifiedArrival, Is.EqualTo(recordedArrival));
            await Task.Delay(TimeSpan.FromSeconds(2.1));
            Assert.That(pending.IsCompleted, Is.False);
        }
        finally { releaseJournal.TrySetResult(); }
        var result = await pending;
        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.LogicalElapsedToArrival, Is.EqualTo(recordedArrival));
    }

    [Test]
    public async Task ArrivalObserverFailureCannotPreventAccountingOrDurableCompletion()
    {
        using var fixture = new ModelFixture();
        var journal = new MemoryJournal();
        using var client = Client(fixture, journal, new FakeHandler("success"));
        client.ResponseArrived += (_, _) => throw new IOException("Injected metadata observer failure.");
        int otherObserverCalls = 0;
        client.ResponseArrived += (_, _) => otherObserverCalls++;
        var result = await client.SendAsync(Request());
        Assert.That(otherObserverCalls, Is.EqualTo(1));
        Assert.That(result.Succeeded, Is.True);
        Assert.That(client.Budget.SettledMicros, Is.EqualTo(2492));
        Assert.That(client.Budget.SlotsHeld, Is.Zero);
        Assert.That(journal.Completions.Single(), Is.EqualTo(result));
    }

    [Test]
    public async Task WrongAccountCurrencyPreventsReservationAndChatCall()
    {
        using var fixture = new ModelFixture();
        var handler = new FakeHandler("usd");
        using var client = Client(fixture, new MemoryJournal(), handler);
        var failure = Assert.ThrowsAsync<ModelException>(async () => await client.SendAsync(Request()));
        Assert.That(failure!.Code, Is.EqualTo("account_currency_unverified"));
        Assert.That(handler.ChatCalls, Is.Zero);
        Assert.That(client.Budget.TotalAttempts, Is.EqualTo(4));
        await Task.CompletedTask;
    }

    [TestCase("model")]
    [TestCase("thinking")]
    [TestCase("stream")]
    [TestCase("max_tokens")]
    [TestCase("too-large")]
    [TestCase("key")]
    public void InvalidRequestCannotReachEvenAccountEndpoint(string mutation)
    {
        using var fixture = new ModelFixture();
        var handler = new FakeHandler("success");
        using var client = Client(fixture, new MemoryJournal(), handler);
        var request = Request();
        var body = JsonNode.Parse(request.InputJson)!.AsObject();
        switch (mutation)
        {
            case "model": body["model"] = "other"; break;
            case "thinking": body["thinking"]!["type"] = "enabled"; break;
            case "stream": body["stream"] = true; break;
            case "max_tokens": body["max_tokens"] = 513; break;
            case "too-large": body["messages"]![0]!["content"] = new string('x', 20_000); break;
            case "key": body["messages"]![0]!["content"] = "Return JSON " + ModelFixture.FakeKey; break;
        }
        Assert.ThrowsAsync<ModelException>(async () => await client.SendAsync(request with { InputJson = body.ToJsonString() }));
        Assert.That(handler.TotalCalls, Is.Zero);
        Assert.That(client.Budget.TotalAttempts, Is.EqualTo(4));
    }

    [Test]
    public async Task ConcurrentRequestsCompleteIndependentlyOfWorldAndNeverHoldLedgerLockAcrossHttp()
    {
        using var fixture = new ModelFixture();
        var handler = new FakeHandler("gated");
        using var client = Client(fixture, new MemoryJournal(), handler);
        var first = client.SendAsync(Request("first"));
        var second = client.SendAsync(Request("second"));
        await handler.TwoChatsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(PersistentBudgetLedger.OpenExisting(fixture.LedgerPath).Snapshot().SlotsHeld, Is.EqualTo(2));
        handler.Release.SetResult();
        var results = await Task.WhenAll(first, second);
        Assert.That(results.All(r => r.Succeeded), Is.True);
        Assert.That(client.Budget.SlotsHeld, Is.Zero);
        Assert.That(client.Budget.SettledMicros, Is.EqualTo(2772));
    }

    private static DeepSeekClient Client(ModelFixture fixture, MemoryJournal journal, FakeHandler handler) =>
        new(new(fixture.CredentialPath, fixture.LedgerPath), journal, handler);

    private static FrozenModelRequest Request(string logicalId = "logical-one") => new(
        new("run", "session", logicalId, "mina", 7), ModelStage.Baseline,
        JsonSerializer.Serialize(new
        {
            model = "deepseek-flash", thinking = new { type = "disabled" }, stream = false,
            max_tokens = 512, response_format = new { type = "json_object" },
            messages = new[] { new { role = "system", content = "Return JSON. Example: {\"operation\":\"wait\"}" } }
        }), false, TimeSpan.FromSeconds(60));

    private sealed class MemoryJournal : IModelJournal
    {
        internal List<ModelDispatch> Requests { get; } = [];
        internal List<ModelCompletion> Completions { get; } = [];
        internal List<string?> Arrivals { get; } = [];
        internal bool FailRequest { get; init; }
        internal bool FailCompletion { get; init; }
        internal string? RejectRequestCode { get; init; }
        internal Func<ModelCompletion, Task>? OnCompletion { get; init; }
        public Task WriteRequestAsync(ModelDispatch dispatch, CancellationToken cancellationToken)
        {
            if (FailRequest) throw new IOException("offline-write-failure");
            lock (Requests) Requests.Add(dispatch);
            if (RejectRequestCode is not null) throw new ModelException(RejectRequestCode);
            return Task.CompletedTask;
        }
        public Task WriteCompletionAsync(ModelCompletion completion, CancellationToken cancellationToken)
        {
            if (FailCompletion) throw new IOException("offline-write-failure");
            lock (Completions) Completions.Add(completion);
            return OnCompletion?.Invoke(completion) ?? Task.CompletedTask;
        }
        public Task WriteResponseArrivalAsync(ModelDispatch dispatch, string? responseBody, TimeSpan elapsed, CancellationToken cancellationToken)
        { lock (Arrivals) Arrivals.Add(responseBody); return Task.CompletedTask; }
    }

    private sealed class FakeHandler(string mode) : HttpMessageHandler
    {
        internal int ChatCalls, TotalCalls;
        internal TaskCompletionSource ChatStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource TwoChatsStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref TotalCalls);
            Assert.That(request.RequestUri!.Host, Is.EqualTo("api.deepseek.com"));
            Assert.That(request.RequestUri.Scheme, Is.EqualTo("https"));
            Assert.That(request.Headers.Authorization!.Parameter, Is.EqualTo(ModelFixture.FakeKey));
            if (request.Method == HttpMethod.Get)
                return Response(200, JsonSerializer.Serialize(new { is_available = true,
                    balance_infos = new[] { new { currency = mode == "usd" ? "USD" : "CNY", total_balance = "not-recorded" } } }));
            int calls = Interlocked.Increment(ref ChatCalls);
            ChatStarted.TrySetResult();
            if (calls == 2) TwoChatsStarted.TrySetResult();
            if (mode == "wait") await Task.Delay(Timeout.Infinite, cancellationToken);
            if (mode == "gated") await Release.Task.WaitAsync(cancellationToken);
            if (mode.StartsWith("http-", StringComparison.Ordinal)) return Response(int.Parse(mode[5..]), "{\"error\":{\"message\":\"discarded-http-body\"}}");
            string content = mode switch
            {
                "empty" => "", "bad-json" => "{",
                "invalid-secret-candidate" => "{\"secret\":\"\\u0066ake-offline-key",
                "secret-echo" => "{\"secret\":\"\\u0066ake-offline-key\"}", _ => "{\"operation\":\"wait\"}"
            };
            var body = new JsonObject
            {
                ["unused"] = "unused-provider-extra",
                ["id"] = Guid.NewGuid().ToString(), ["object"] = "chat.completion",
                ["model"] = mode == "model-mismatch" ? "unregistered-model" : "deepseek-flash",
                ["system_fingerprint"] = mode == "fingerprint-mismatch" ? "different" : "aeb56401ca74e127821c4f9126dcb669",
                ["choices"] = JsonSerializer.SerializeToNode(new[] { new { index = 0,
                    finish_reason = mode == "invalid-secret-finish" ? ModelFixture.FakeKey : mode is "length" or "aborted" ? mode : "stop",
                    message = new { role = "assistant", content, reasoning_content = "discarded-reasoning" } } }),
                ["usage"] = JsonSerializer.SerializeToNode(new { prompt_tokens = 100, completion_tokens = 10,
                    total_tokens = 110, prompt_cache_hit_tokens = 70, prompt_cache_miss_tokens = mode == "invalid-cache" ? 31 : 30 })
            };
            if (mode == "no-usage") body.Remove("usage");
            if (mode == "no-fingerprint") body.Remove("system_fingerprint");
            string json = body.ToJsonString();
            if (mode == "duplicate-response-field") json = "{\"id\":\"duplicate\"," + json[1..];
            if (mode == "duplicate-unused-field") json = json[..^1] + ",\"extra\":1,\"extra\":2}";
            return Response(200, json);
        }
        private static HttpResponseMessage Response(int status, string body) =>
            new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
