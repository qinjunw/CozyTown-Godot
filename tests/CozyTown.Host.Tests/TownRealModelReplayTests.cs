using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CozyTown.Core.Town;
using CozyTown.Game;
using CozyTown.Model;
using NUnit.Framework;

namespace CozyTown.Host.Tests;

[TestFixture]
public sealed class TownRealModelReplayTests
{
    [TestCase("success", null, "full-demo-v2", "mina")]
    [TestCase("duplicate", "duplicate_json_property", "full-demo-v2", "mina")]
    [TestCase("malformed", "invalid_json", "full-demo-v2", "mina")]
    [TestCase("http-error", "provider_http_failure", "full-demo-v2", "mina")]
    [TestCase("success", null, "manual-play-v1", "mina")]
    [TestCase("success", null, "manual-play-v1", "eli")]
    [TestCase("success", null, "manual-play-v1", "ren")]
    [TestCase("success", null, "manual-play-v1", "sora")]
    [TestCase("malformed", "invalid_json", "manual-play-v1", "mina")]
    [TestCase("http-error", "provider_http_failure", "manual-play-v1", "mina")]
    public async Task ProviderArrivalAndExportKeepReplayFactsWithoutReasoningOrErrorBodies(string mode, string? expectedFailure, string profileVersion, string residentId)
    {
        string root = Path.Combine(Path.GetTempPath(), "cozytown-response-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string credentials = Path.Combine(root, "credentials.json"), ledgerPath = Path.Combine(root, "ledger.json");
            File.WriteAllText(credentials, "{\"test\":{\"Todo\":\"CozyTown\",\"testingAPIKey\":\"fixed-offline-key\"}}");
            var runtime = TownModelRuntimeProfile.Resolve(profileVersion, "manual");
            var ledger = PersistentBudgetLedger.InitializeNew(ledgerPath, profileVersion);
            var content = TownPlayerDialogueReplayTests.TownPlayerDialogueTestsContent();
            var world = new TownWorld(content, budgetId: ledger.Profile.BudgetId);
            world.SetPlayerPosition(world.State.Residents.Single(r => r.Id == residentId).Position);
            var input = new TownInputHost(content, world);
            using var journal = new TownRunJournal(Path.Combine(root, "runs"), world.State.BranchId, new { schemaVersion = 2 });
            journal.StartReplay(content, world, input); journal.SetReplayBudget(ledger.Snapshot(), runtime.ReplayProfile);
            var dialogue = new TownDialogueHost(world); dialogue.Open(residentId, []); input.Pause("dialogue", true);
            var request = dialogue.Generate(input.State.PauseReasons)!;
            var frozen = dialogue.Freeze(request, runtime.NewRequestStage("baseline", "player_text"));
            input.FreezeRequest(request.LogicalRequestId, TownJson.Serialize(frozen));
            journal.BeforeSendOnHost = dispatch =>
            {
                Assert.That(dialogue.Acknowledge(request.PlayerSessionId, request.LogicalRequestId, dispatch.AttemptId), Is.True);
                return Task.CompletedTask;
            };
            using var client = new DeepSeekClient(runtime.ConfigureProvider(new(credentials, ledgerPath, "registered", profileVersion)), journal, new ProviderHandler(mode));
            client.ResponseArrived += (dispatch, elapsed) => world.MarkPlayerDialogueResponseArrived(world.SessionId,
                request.PlayerSessionId, request.LogicalRequestId, dispatch.AttemptId, elapsed.TotalSeconds);
            var completion = await client.SendAsync(frozen);
            journal.RecordReplayInput("model_mailbox_enqueued", new { dispatch = completion.Dispatch });
            journal.RecordReplayInput("model_mailbox_entered", new { dispatch = completion.Dispatch });
            dialogue.Complete(request, world.SessionId, completion.Dispatch.AttemptId, completion.CandidateJson,
                "real_model", completion.FailureCode, completion.LogicalElapsedToArrival!.Value.TotalSeconds);
            dialogue.Present(input.State.PauseReasons);
            Assert.That(completion.FailureCode, Is.EqualTo(expectedFailure));
            Assert.That(dialogue.Current!.ResidentId, Is.EqualTo(residentId));
            Assert.That(dialogue.Current.Displays.Last().Source, Is.EqualTo(expectedFailure is null ? "real_model" : "rule_fallback"));
            Assert.That(ledger.Snapshot().Attempts.Single().Stage, Is.EqualTo("manual"));
            var package = journal.CaptureReplay();
            Assert.That(TownReplay.Run(package).Passed, Is.True, TownJson.Serialize(TownReplay.Run(package)));
            string archivePath = Path.Combine(root, "export.zip"); journal.ExportSnapshotZip(archivePath);
            using var archive = ZipFile.OpenRead(archivePath);
            string exported = string.Join("\n", archive.Entries.Select(entry => { using var reader = new StreamReader(entry.Open()); return reader.ReadToEnd(); }));
            Assert.That(exported, Does.Not.Contain("private-reasoning").And.Not.Contain("unused-extra").And.Not.Contain("private-error-body").And.Not.Contain("fixed-offline-key"));
            if (mode == "success")
            {
                foreach (string field in new[] { "systemFingerprint", "expectedSystemFingerprint", "executionPurpose", "providerWarningCode", "generationEnded", "usage" })
                {
                    var tampered = AlterCompletion(package, node => node[field] = field switch
                    {
                        "generationEnded" => JsonValue.Create(false), "usage" => null, _ => JsonValue.Create("changed")
                    });
                    Assert.That(TownReplay.Run(tampered).Passed, Is.False, "Unbound completion field: " + field);
                }
            }
            else
                Assert.That(TownReplay.Run(AlterCompletion(package, node => node["failureCode"] = null)).Passed, Is.False);
        }
        finally
        {
            string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("test_cleanup_scope_invalid");
            Directory.Delete(root, true);
        }
    }

    private static TownReplayPackage AlterCompletion(TownReplayPackage package, Action<JsonNode> change)
    {
        var copy = TownJson.Clone(package);
        int index = Array.FindIndex(copy.Boundaries, b => b.Command == "model_completion_persisted");
        var trace = copy.Boundaries[index]; var node = JsonNode.Parse(trace.InputJson)!; change(node);
        string json = node.ToJsonString();
        copy.Boundaries[index] = trace with { InputJson = json, InputFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trace.Command + "\n" + json))).ToLowerInvariant() };
        return copy;
    }

    private sealed class ProviderHandler(string mode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Response(200, "{\"is_available\":true,\"balance_infos\":[{\"currency\":\"CNY\"}]}"));
            if (mode == "http-error") return Task.FromResult(Response(502, "private-error-body"));
            string body = TownJson.Serialize(new
            {
                id = "fixed-response", model = "deepseek-flash", @object = "chat.completion", system_fingerprint = "changed-provider",
                choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant",
                    content = mode == "malformed" ? "{" : "{\"text\":\"今天想去河边走一走。\"}", reasoning_content = "private-reasoning" } } },
                usage = new { prompt_tokens = 100, completion_tokens = 10, total_tokens = 110, prompt_cache_hit_tokens = 0, prompt_cache_miss_tokens = 100 },
                unused = "unused-extra"
            });
            if (mode == "duplicate") body = "{\"id\":\"duplicate\"," + body[1..];
            return Task.FromResult(Response(200, body));
        }
        private static HttpResponseMessage Response(int status, string body) => new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
