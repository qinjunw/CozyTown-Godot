using System.Net;
using System.Text;
using System.Text.Json;

namespace ModelProbe;

internal static class SelfTests
{
    public static async Task<int> RunAsync()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "self-test-results", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int passed = 0;
        void Test(string name, Action body)
        {
            body(); passed++; Console.WriteLine("self_test_passed:" + name);
        }
        string LedgerPath(string name) => Path.Combine(root, name, "ledger.json");
        Test("persistent_reservations_and_concurrency", () =>
        {
            string path = LedgerPath("persistent");
            var first = new BudgetLedger(path);
            first.Reserve("probe", "one");
            var reopened = new BudgetLedger(path);
            Check(reopened.Snapshot().Attempts.Count == 1);
            reopened.Reserve("probe", "two");
            Check(reopened.Snapshot().Attempts.Sum(a => a.ReservedCostMicros) == 4_220_000);
            Fails("concurrency_limit", () => first.Reserve("probe", "three"));
        });
        Test("idempotent_settlement_and_conflict", () =>
        {
            var ledger = new BudgetLedger(LedgerPath("settlement"));
            var a = ledger.Reserve("probe", "one");
            ledger.Settle(a.AttemptId, 100, 10);
            ledger.Settle(a.AttemptId, 100, 10);
            var result = ledger.Snapshot().Attempts.Single();
            Check(result.SettledCostMicros == 280 && result.ReservedCostMicros == 0 && !result.SlotHeld);
            Fails("settlement_conflict", () => ledger.Settle(a.AttemptId, 101, 10));
            Check(ledger.Snapshot().Attempts.Single().SettledCostMicros == 280);
        });
        Test("stage_and_total_limits_persist", () =>
        {
            string path = LedgerPath("limits");
            var ledger = new BudgetLedger(path);
            foreach (var stage in new[] { ("probe", 4), ("baseline", 22), ("counterfactual", 22) })
            {
                for (int i = 0; i < stage.Item2; i++)
                {
                    var a = ledger.Reserve(stage.Item1, stage.Item1 + i);
                    ledger.Settle(a.AttemptId, 10, 1);
                }
                Fails(stage.Item1 == "counterfactual" ? "total_limit" : "stage_limit",
                    () => new BudgetLedger(path).Reserve(stage.Item1, "excess"));
            }
            Check(new BudgetLedger(path).Snapshot().Attempts.Count == 48);
        });
        Test("settled_plus_all_reserves_enforce_money_limit", () =>
        {
            var ledger = new BudgetLedger(LedgerPath("money"));
            var a = ledger.Reserve("probe", "one");
            ledger.Settle(a.AttemptId, 1_048_576, 512);
            var b = ledger.Reserve("probe", "two");
            Fails("money_limit", () => ledger.Reserve("probe", "three"));
            ledger.Settle(b.AttemptId, 1_048_576, 512);
            Fails("money_limit", () => ledger.Reserve("probe", "four"));
            Check(ledger.Snapshot().Attempts.Count == 2);
        });
        Test("unknown_occupancy_is_retained", () =>
        {
            string path = LedgerPath("unknown");
            var ledger = new BudgetLedger(path);
            var a = ledger.Reserve("probe", "one");
            ledger.MarkUnknown(a.AttemptId, "transport_timeout", false);
            var reopened = new BudgetLedger(path);
            var item = reopened.Snapshot().Attempts.Single();
            Check(item.SlotHeld && item.ReservedCostMicros == 2_110_000 && item.SettledCostMicros == 0);
            Fails("ledger_stopped", () => reopened.Reserve("probe", "two"));
        });
        Test("complete_generation_without_usage_keeps_money", () =>
        {
            var ledger = new BudgetLedger(LedgerPath("no-usage"));
            var a = ledger.Reserve("probe", "one");
            ledger.MarkUnknown(a.AttemptId, "usage_missing", true);
            var item = ledger.Snapshot().Attempts.Single();
            Check(!item.SlotHeld && item.ReservedCostMicros == 2_110_000);
        });
        Test("recovery_quota_survives_reopening", () =>
        {
            string path = LedgerPath("recovery");
            var ledger = new BudgetLedger(path);
            var a = ledger.Reserve("baseline", "persistent-logical-id", true);
            ledger.Settle(a.AttemptId, 10, 1);
            Fails("recovery_limit", () => new BudgetLedger(path).Reserve("baseline", "persistent-logical-id", true));
        });
        Test("file_lock_excludes_another_handle", () =>
        {
            string path = LedgerPath("locking");
            _ = new BudgetLedger(path);
            using var held = new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            bool rejected = false;
            try { using var duplicate = new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { rejected = true; }
            Check(rejected);
        });
        Test("missing_ledger_is_not_reset", () =>
        {
            string path = LedgerPath("missing");
            _ = new BudgetLedger(path);
            File.Move(path, path + ".removed-for-test");
            Fails("ledger_missing_existing_lock", () => _ = new BudgetLedger(path));
        });
        Test("credential_reader_fake_values_only", () =>
        {
            string path = Path.Combine(root, "fake-credentials.json");
            File.WriteAllText(path, "{\"other\":{\"Todo\":\"Other\",\"testingAPIKey\":\"ignore\"},\"entry\":{\"Todo\":\"CozyTown\",\"testingAPIKey\":\"fake-test-key\",\"endpoint\":\"https://never-used.invalid\"}}");
            Check(CredentialReader.Read(path) == "fake-test-key");
            File.WriteAllText(path, "{\"a\":{\"Todo\":\"CozyTown\",\"testingAPIKey\":\"fake-one\"},\"b\":{\"Todo\":\"CozyTown\",\"testingAPIKey\":\"fake-two\"}}");
            Fails("credential_match_count_invalid", () => CredentialReader.Read(path));
            File.WriteAllText(path, "{\"entry\":{\"Todo\":\"CozyTown\",\"testingAPIKey\":\" \"}}");
            Fails("credential_key_invalid", () => CredentialReader.Read(path));
            File.WriteAllText(path, "{\"entry\":{\"Todo\":\"CozyTown\",\"testingAPIKey\":\"one\",\"testingAPIKey\":\"two\"}}");
            Fails("credential_fields_ambiguous", () => CredentialReader.Read(path));
        });
        Test("candidate_targets_and_short_text", () =>
        {
            var context = ProbeRunner.Contexts[0];
            _ = ProbeRunner.ValidateCandidate("{\"operation\":\"wait\"}", context);
            _ = ProbeRunner.ValidateCandidate("{\"operation\":\"invite\",\"targetId\":\"eli\"}", context);
            _ = ProbeRunner.ValidateCandidate("{\"operation\":\"approach\",\"targetId\":\"ren\"}", context);
            Fails("candidate_target_invalid", () => ProbeRunner.ValidateCandidate("{\"operation\":\"invite\",\"targetId\":\"unknown\"}", context));
            Fails("candidate_operation_invalid", () => ProbeRunner.ValidateCandidate("{\"operation\":\"set_money\"}", context));
            Fails("candidate_text_invalid", () => ProbeRunner.ValidateCandidate(JsonSerializer.Serialize(new { operation = "wait", text = new string('a', 201) }), context));
        });
        Test("fixed_http_configuration", () =>
        {
            using var client = ProbeRunner.CreateClient(new FakeHandler("success"));
            Check(client.BaseAddress!.AbsoluteUri == "https://api.deepseek.com/"
                && client.Timeout == TimeSpan.FromSeconds(30) && client.MaxResponseContentBufferSize == 1_048_576);
        });
        Test("evidence_redacts_escaped_fake_secret", () =>
        {
            string directory = Path.Combine(root, "escaped-secret-output");
            string fakeSecret = "fake-<\"\\unicode-\u03c0";
            var writer = new EvidenceWriter(directory) { Secret = fakeSecret };
            writer.Write("evidence.json", new
            {
                direct = fakeSecret,
                nested = new[] { new { echo = "prefix:" + fakeSecret + ":suffix" } },
                providerJson = JsonSerializer.Serialize(new { echo = fakeSecret }),
                candidateBody = "{\"operation\":\"wait\",\"reason\":\"\\u0066ake-\\u003c\\u0022\\u005cunicode-\\u03c0\"}",
                unchangedBody = "{ \"operation\" : \"wait\" }"
            });
            string text = File.ReadAllText(Path.Combine(directory, "evidence.json"));
            using var document = JsonDocument.Parse(text);
            Check(document.RootElement.GetProperty("direct").GetString() == "[REDACTED]");
            Check(!document.RootElement.GetProperty("nested")[0].GetProperty("echo").GetString()!.Contains(fakeSecret));
            using var nested = JsonDocument.Parse(document.RootElement.GetProperty("providerJson").GetString()!);
            Check(nested.RootElement.GetProperty("echo").GetString() == "[REDACTED]");
            using var candidate = JsonDocument.Parse(document.RootElement.GetProperty("candidateBody").GetString()!);
            Check(candidate.RootElement.GetProperty("reason").GetString() == "[REDACTED]");
            Check(document.RootElement.GetProperty("unchangedBody").GetString() == "{ \"operation\" : \"wait\" }");
        });

        foreach (string mode in new[] { "success", "http-502", "no-usage", "length", "model-mismatch", "fingerprint-mismatch", "usd", "bad-candidate" })
        {
            string directory = Path.Combine(root, "runner-" + mode);
            Directory.CreateDirectory(directory);
            string credential = Path.Combine(directory, "fake-credentials.json");
            File.WriteAllText(credential, "{\"entry\":{\"Todo\":\"CozyTown\",\"testingAPIKey\":\"fake-test-key\"}}");
            string ledgerPath = Path.Combine(directory, "ledger.json");
            string output = Path.Combine(directory, "output");
            var handler = new FakeHandler(mode);
            int exit = await ProbeRunner.RunAsync(new(credential, ledgerPath, output), handler);
            Check(exit == (mode == "success" ? 0 : 1));
            Check(handler.ChatCalls == (mode == "success" ? 4 : mode == "usd" ? 0 : mode == "fingerprint-mismatch" ? 2 : 1));
            string evidence = string.Join("\n", Directory.GetFiles(output, "*.json").Select(File.ReadAllText));
            Check(!evidence.Contains("fake-test-key") && !evidence.Contains("DO_NOT_RECORD_BALANCE") && !evidence.Contains("DO_NOT_RECORD_REASONING"));
            var snapshot = new BudgetLedger(ledgerPath).Snapshot();
            if (mode == "http-502") Check(snapshot.Attempts.Single().SlotHeld && snapshot.Attempts.Single().ReservedCostMicros == 2_110_000);
            if (mode == "no-usage") Check(!snapshot.Attempts.Single().SlotHeld && snapshot.Attempts.Single().ReservedCostMicros == 2_110_000);
            if (mode is "length" or "bad-candidate") Check(snapshot.Attempts.Single().Status == "settled");
            if (mode is "model-mismatch" or "fingerprint-mismatch")
                Check(snapshot.Attempts.Last().Status == "unknown" && snapshot.Attempts.Last().ReservedCostMicros == 2_110_000
                    && !snapshot.Attempts.Last().SlotHeld && snapshot.Attempts.Last().SettledCostMicros == 0);
            Fails("output_already_exists", () => _ = new EvidenceWriter(output));
            passed++; Console.WriteLine("self_test_passed:fake_http_" + mode);
        }
        File.WriteAllText(Path.Combine(root, "self-test-summary.json"), JsonSerializer.Serialize(new
        {
            passed, remoteRequests = 0, realCredentialsRead = false, scope = "offline assertions and in-memory HTTP handler only"
        }, JsonData.Options));
        Console.WriteLine($"self_tests_passed:{passed}; remote_requests:0");
        return 0;
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new ProbeException("self_test_assertion_failed");
    }
    private static void Fails(string code, Action body)
    {
        try { body(); }
        catch (ProbeException e) when (e.Code == code) { return; }
        throw new ProbeException("self_test_expected_failure_missing");
    }

    private sealed class FakeHandler(string mode) : HttpMessageHandler
    {
        public int ChatCalls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.RequestUri!.Host == "api.deepseek.com");
            if (request.Method == HttpMethod.Get)
                return Response(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    is_available = true, balance_infos = new[] { new { currency = mode == "usd" ? "USD" : "CNY", total_balance = "DO_NOT_RECORD_BALANCE" } }
                }));
            ChatCalls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Check(body.RootElement.GetProperty("model").GetString() == "deepseek-flash"
                && body.RootElement.GetProperty("thinking").GetProperty("type").GetString() == "disabled"
                && !body.RootElement.GetProperty("stream").GetBoolean()
                && body.RootElement.GetProperty("max_tokens").GetInt32() == 512);
            if (mode == "http-502") return Response(HttpStatusCode.BadGateway, "{\"error\":\"mock_gateway_failure\"}");
            var payload = new Dictionary<string, object?>
            {
                ["id"] = "fake-generation-" + ChatCalls, ["object"] = "chat.completion",
                ["model"] = mode == "model-mismatch" ? "unregistered-model" : "deepseek-flash",
                ["system_fingerprint"] = mode == "fingerprint-mismatch" && ChatCalls == 2 ? "fp-changed" : "fp-fixture",
                ["choices"] = new[] { new { index = 0, finish_reason = mode == "length" ? "length" : "stop",
                    message = new { role = "assistant", reasoning_content = "DO_NOT_RECORD_REASONING", content = mode == "bad-candidate" ? "{\"operation\":\"set_money\"}" : "{\"operation\":\"wait\",\"reason\":\"I choose to rest.\"}" } } }
            };
            if (mode != "no-usage") payload["usage"] = new { prompt_tokens = 100, completion_tokens = 10, total_tokens = 110 };
            return Response(HttpStatusCode.OK, JsonSerializer.Serialize(payload));
        }
        private static HttpResponseMessage Response(HttpStatusCode code, string body) =>
            new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
