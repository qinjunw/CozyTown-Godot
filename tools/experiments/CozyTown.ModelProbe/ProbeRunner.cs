using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ModelProbe;

internal static class ProbeRunner
{
    private const string Model = "deepseek-flash";
    private const int ResponseLimit = 1024 * 1024;
    private static readonly string[] FinishedReasons = ["stop", "length", "content_filter", "tool_calls", "insufficient_system_resource", "aborted"];
    private const string SystemPrompt = "This is a synthetic interface probe, not a live game. Choose one proposed action. Return only a JSON object with operation: wait, approach, or invite. For approach/invite, targetId must be one of visibleResidents; for wait omit targetId or use null. Optional text and reason must each be a single line of at most 200 characters. Do not add fields. You cannot execute actions or change world state. Example: {\"operation\":\"wait\",\"reason\":\"I will rest.\"}";

    public static HttpClient CreateClient(HttpMessageHandler? testHandler = null) => new(testHandler ?? new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None
    })
    {
        BaseAddress = new Uri("https://api.deepseek.com/"),
        Timeout = TimeSpan.FromSeconds(30),
        MaxResponseContentBufferSize = ResponseLimit
    };

    public static async Task<int> RunAsync(ProbeOptions options, HttpMessageHandler? testHandler = null)
    {
        if (string.Equals(options.CredentialFile, options.LedgerFile, StringComparison.OrdinalIgnoreCase))
            throw new ProbeException("credential_ledger_path_conflict");
        var evidence = new EvidenceWriter(options.OutputDirectory);
        BudgetLedger? ledger = null;
        var report = new RunReport { ConfigurationFingerprint = Fingerprint(BuildConfiguration()) };
        string? observedFingerprint = null;
        bool fingerprintInitialized = false;
        try
        {
            string key = CredentialReader.Read(options.CredentialFile);
            evidence.Secret = key;
            ledger = new BudgetLedger(options.LedgerFile);
            var previous = ledger.Snapshot();
            if (previous.StopReason != null) throw new ProbeException("ledger_stopped");
            if (previous.Attempts.Any(a => a.SlotHeld || a.ReservedCostMicros != 0))
                throw new ProbeException("prior_attempt_unresolved");
            if (previous.Attempts.Count(a => a.Stage == "probe") != 0)
                throw new ProbeException("probe_stage_already_started");

            using var client = CreateClient(testHandler);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using (var balance = await client.GetAsync("user/balance", HttpCompletionOption.ResponseContentRead))
            {
                report.BalanceHttpStatus = (int)balance.StatusCode;
                if (!balance.IsSuccessStatusCode) throw new ProbeException("balance_http_failure");
                var check = ParseBalance(await ReadBodyAsync(balance));
                report.Currency = check.Currency;
                report.AccountAvailable = check.Available;
                evidence.Write("balance-check.json", new { currency = check.Currency, available = check.Available });
                if (check.Currency != "CNY") throw new ProbeException("balance_currency_unverified");
                if (!check.Available) throw new ProbeException("balance_unavailable");
            }

            for (int index = 0; index < Contexts.Length; index++)
            {
                ProbeContext context = Contexts[index];
                string requestBody = BuildRequest(context);
                if (Encoding.UTF8.GetByteCount(requestBody) > 16 * 1024)
                    throw new ProbeException("request_too_large");
                var attempt = ledger.Reserve("probe", "interface-probe-" + context.ResidentId);
                var item = new AttemptEvidence
                {
                    AttemptId = attempt.AttemptId, ResidentId = context.ResidentId,
                    PreSendUtc = DateTimeOffset.UtcNow, RequestFingerprint = Fingerprint(requestBody),
                    ConfigurationFingerprint = report.ConfigurationFingerprint
                };
                report.Attempts.Add(item);
                bool settled = false;
                bool completeGeneration = false;
                Stopwatch? duration = null;
                try
                {
                    evidence.Write($"{index + 1:D2}-request.json", new
                    {
                        budgetId = BudgetLedger.BudgetId, stage = "probe", attemptId = attempt.AttemptId,
                        logicalRequestId = attempt.LogicalRequestId, residentId = context.ResidentId,
                        syntheticContext = true, preSendUtc = item.PreSendUtc,
                        configurationFingerprint = item.ConfigurationFingerprint, requestFingerprint = item.RequestFingerprint,
                        request = JsonSerializer.Deserialize<JsonElement>(requestBody)
                    });
                    using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
                        { Content = new StringContent(requestBody, Encoding.UTF8, "application/json") };
                    item.DispatchStartedAtUtc = DateTimeOffset.UtcNow;
                    duration = Stopwatch.StartNew();
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead);
                    item.HttpStatus = (int)response.StatusCode;
                    string responseBody = await ReadBodyAsync(response);
                    item.ArrivedAtUtc = DateTimeOffset.UtcNow;
                    item.DurationMilliseconds = duration.ElapsedMilliseconds;
                    if (!response.IsSuccessStatusCode)
                    {
                        CaptureErrorCodes(responseBody, item);
                        throw new ProbeException("chat_http_failure");
                    }
                    using var document = JsonDocument.Parse(responseBody);
                    JsonData.RequireUniqueProperties(document.RootElement);
                    var generation = ParseGeneration(document.RootElement);
                    completeGeneration = true;
                    item.ProviderRequestId = generation.Id;
                    item.ReturnedModel = generation.Model;
                    item.FinishReason = generation.FinishReason;
                    item.SystemFingerprint = generation.SystemFingerprint;
                    item.CandidateBody = generation.Content;
                    item.ReportedUsage = UsageEvidence(document.RootElement);
                    if (generation.Model != Model) throw new ProbeException("returned_model_mismatch");
                    if (fingerprintInitialized && generation.SystemFingerprint != observedFingerprint)
                        throw new ProbeException("system_fingerprint_changed");
                    observedFingerprint = generation.SystemFingerprint;
                    fingerprintInitialized = true;
                    var usage = ParseUsage(document.RootElement);
                    ledger.Settle(attempt.AttemptId, usage.Prompt, usage.Completion);
                    settled = true;
                    item.PromptTokens = usage.Prompt;
                    item.CompletionTokens = usage.Completion;
                    item.ConservativeCostMicros = BudgetLedger.CalculateCost(usage.Prompt, usage.Completion);
                    if (generation.FinishReason != "stop") throw new ProbeException("candidate_generation_incomplete");
                    item.Candidate = ValidateCandidate(generation.Content, context);
                    item.Status = "passed";
                }
                catch (Exception error)
                {
                    item.FailureCode = FailureCode(error);
                    item.Status = "failed";
                    item.DurationMilliseconds ??= duration?.ElapsedMilliseconds;
                    if (!settled) ledger.MarkUnknown(attempt.AttemptId, item.FailureCode, completeGeneration);
                    else ledger.Stop(item.FailureCode);
                    evidence.Write($"{index + 1:D2}-result.json", item);
                    Console.WriteLine($"probe_result:{index + 1}; status:failed; code:{item.FailureCode}");
                    throw new ProbeException(item.FailureCode);
                }
                evidence.Write($"{index + 1:D2}-result.json", item);
                Console.WriteLine($"probe_result:{index + 1}; status:passed; cost_micros:{item.ConservativeCostMicros}");
            }
            report.Status = "passed";
            report.Scope = "Four synthetic interface probes only; no Godot scene or resident autonomy validation.";
            report.Ledger = ledger.Snapshot();
            evidence.Write("report.json", report);
            Console.WriteLine("probe_passed:4_synthetic_interface_samples");
            return 0;
        }
        catch (Exception error)
        {
            report.Status = "failed";
            report.FailureCode = FailureCode(error);
            if (ledger != null)
            {
                try { ledger.Stop(report.FailureCode); report.Ledger = ledger.Snapshot(); }
                catch { report.LedgerDiagnostic = "ledger_update_failed_reservations_not_released"; }
            }
            try { evidence.Write("report.json", report); }
            catch { Console.Error.WriteLine("probe_evidence_write_failed"); }
            Console.Error.WriteLine("probe_failed:" + report.FailureCode);
            return 1;
        }
    }

    internal static (string Currency, bool Available) ParseBalance(string body)
    {
        using var doc = JsonDocument.Parse(body);
        JsonData.RequireUniqueProperties(doc.RootElement);
        var root = doc.RootElement;
        if (!root.TryGetProperty("is_available", out var available)
            || (available.ValueKind != JsonValueKind.True && available.ValueKind != JsonValueKind.False)
            || !root.TryGetProperty("balance_infos", out var infos) || infos.ValueKind != JsonValueKind.Array
            || infos.GetArrayLength() != 1 || !infos[0].TryGetProperty("currency", out var currency)
            || currency.ValueKind != JsonValueKind.String)
            throw new ProbeException("balance_currency_unverified");
        // Amount fields are intentionally neither read nor retained.
        return (currency.GetString()!, available.GetBoolean());
    }

    internal static (long Prompt, long Completion) ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object
            || !usage.TryGetProperty("prompt_tokens", out var prompt) || !prompt.TryGetInt64(out long p)
            || !usage.TryGetProperty("completion_tokens", out var completion) || !completion.TryGetInt64(out long c)
            || !usage.TryGetProperty("total_tokens", out var total) || !total.TryGetInt64(out long t)
            || p < 0 || c < 0 || p > 1_048_576 || c > 512 || t != p + c)
            throw new ProbeException("usage_invalid_or_missing");
        return (p, c);
    }

    internal static Generation ParseGeneration(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())
            || !root.TryGetProperty("object", out var kind) || kind.GetString() != "chat.completion"
            || !root.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1)
            throw new ProbeException("generation_terminal_unverified");
        var choice = choices[0];
        if (!choice.TryGetProperty("index", out var index) || !index.TryGetInt32(out int i) || i != 0
            || !choice.TryGetProperty("finish_reason", out var finish) || finish.ValueKind != JsonValueKind.String
            || !FinishedReasons.Contains(finish.GetString()) || !choice.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("role", out var role)
            || role.GetString() != "assistant")
            throw new ProbeException("generation_terminal_unverified");
        string? content = message.TryGetProperty("content", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString() : null;
        string? fingerprint = null;
        if (root.TryGetProperty("system_fingerprint", out var systemFingerprint) && systemFingerprint.ValueKind != JsonValueKind.Null)
        {
            if (systemFingerprint.ValueKind != JsonValueKind.String) throw new ProbeException("system_fingerprint_invalid");
            fingerprint = systemFingerprint.GetString();
        }
        return new(id.GetString()!, model.GetString()!, finish.GetString()!, content, fingerprint);
    }

    internal static JsonElement ValidateCandidate(string? content, ProbeContext context)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new ProbeException("candidate_empty");
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;
        JsonData.RequireUniqueProperties(root);
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p =>
                p.Name != "operation" && p.Name != "targetId" && p.Name != "text" && p.Name != "reason")
            || !root.TryGetProperty("operation", out var operation) || operation.ValueKind != JsonValueKind.String)
            throw new ProbeException("candidate_structure_invalid");
        string op = operation.GetString()!;
        bool hasTarget = root.TryGetProperty("targetId", out var target) && target.ValueKind != JsonValueKind.Null;
        if (op == "wait")
        {
            if (hasTarget) throw new ProbeException("candidate_wait_target_invalid");
        }
        else if (op == "approach" || op == "invite")
        {
            if (!hasTarget || target.ValueKind != JsonValueKind.String || !context.VisibleResidents.Contains(target.GetString()))
                throw new ProbeException("candidate_target_invalid");
        }
        else throw new ProbeException("candidate_operation_invalid");
        foreach (string field in new[] { "text", "reason" })
            if (root.TryGetProperty(field, out var value)
                && (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > 200
                    || value.GetString()!.Any(char.IsControl)))
                throw new ProbeException("candidate_text_invalid");
        return root.Clone();
    }

    internal static string BuildRequest(ProbeContext context) => JsonSerializer.Serialize(new
    {
        model = Model, thinking = new { type = "disabled" }, stream = false, max_tokens = 512,
        response_format = new { type = "json_object" },
        messages = new[]
        {
            new { role = "system", content = SystemPrompt },
            new { role = "user", content = JsonSerializer.Serialize(context, JsonData.Options) }
        }
    });

    private static string BuildConfiguration() => JsonSerializer.Serialize(new
    {
        endpoint = "https://api.deepseek.com/chat/completions", model = Model, thinking = "disabled",
        stream = false, maxTokens = 512, responseFormat = "json_object", systemPrompt = SystemPrompt,
        inputBytesLimit = 16 * 1024, responseBytesLimit = ResponseLimit, timeoutSeconds = 30
    });
    private static string Fingerprint(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static object? UsageEvidence(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        var values = new Dictionary<string, object?>();
        foreach (string name in new[] { "prompt_tokens", "completion_tokens", "total_tokens", "prompt_cache_hit_tokens", "prompt_cache_miss_tokens" })
            if (usage.TryGetProperty(name, out var value))
                values[name] = value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)
                    ? number : "invalid_type_or_range";
        return values;
    }

    private static void CaptureErrorCodes(string body, AttemptEvidence item)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object) return;
            foreach (string name in new[] { "code", "type" })
                if (error.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    string text = value.GetString()!;
                    if (text.Length <= 100 && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
                    {
                        if (name == "code") item.ProviderErrorCode = text;
                        else item.ProviderErrorType = text;
                    }
                }
        }
        catch (JsonException) { }
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response)
    {
        byte[] bytes = await response.Content.ReadAsByteArrayAsync();
        if (bytes.Length > ResponseLimit) throw new ProbeException("response_too_large");
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static string FailureCode(Exception error) => error switch
    {
        ProbeException probe => probe.Code,
        TaskCanceledException => "transport_timeout_or_cancelled",
        HttpRequestException => "transport_failure",
        JsonException => "invalid_json",
        IOException => "file_io_failure",
        _ => "unexpected_failure"
    };

    internal static readonly ProbeContext[] Contexts =
    [
        new("mina", "Shopkeeper on a short break; may wait or approach/invite a visible resident.", ["eli", "ren"]),
        new("eli", "Farmer resting after work; no existing social promise; may wait or seek company.", ["mina", "sora"]),
        new("ren", "Fisher taking a break; may wait, approach, or invite someone visible.", ["sora", "mina"]),
        new("sora", "Cook with free time before the next duty; may wait or contact a visible resident.", ["ren", "eli"])
    ];
}

internal sealed record ProbeContext(string ResidentId, string OwnState, string[] VisibleResidents);
internal sealed record Generation(string Id, string Model, string FinishReason, string? Content, string? SystemFingerprint);

internal sealed class EvidenceWriter
{
    private readonly string _directory;
    public string? Secret { private get; set; }
    public EvidenceWriter(string directory)
    {
        _directory = Path.GetFullPath(directory);
        if (Directory.Exists(_directory) || File.Exists(_directory)) throw new ProbeException("output_already_exists");
        Directory.CreateDirectory(_directory);
        using var marker = new FileStream(Path.Combine(_directory, ".claimed"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        marker.Flush(true);
    }
    public void Write(string name, object value)
    {
        JsonNode? node = JsonSerializer.SerializeToNode(value, JsonData.Options);
        string text = Sanitize(node)?.ToJsonString(JsonData.Options) ?? "null";
        using var stream = new FileStream(Path.Combine(_directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough);
        stream.Write(Encoding.UTF8.GetBytes(text));
        stream.Flush(true);
    }

    private JsonNode? Sanitize(JsonNode? node)
    {
        if (string.IsNullOrEmpty(Secret)) return node;
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            string trimmed = text.TrimStart();
            if (trimmed.StartsWith('{') || trimmed.StartsWith('[') || trimmed.StartsWith('"'))
            {
                try
                {
                    JsonNode? embedded = JsonNode.Parse(text);
                    string before = embedded?.ToJsonString(JsonData.Options) ?? "null";
                    string after = Sanitize(embedded)?.ToJsonString(JsonData.Options) ?? "null";
                    // Preserve exact request/candidate text unless a decoded secret was removed.
                    if (after != before) return JsonValue.Create(after);
                }
                catch (JsonException) { }
            }
            string escaped = JsonSerializer.Serialize(Secret)[1..^1];
            return JsonValue.Create(text.Replace(Secret, "[REDACTED]", StringComparison.Ordinal)
                .Replace(escaped, "[REDACTED]", StringComparison.Ordinal));
        }
        if (node is JsonObject obj)
            foreach (var property in obj.ToArray())
            {
                var sanitized = Sanitize(property.Value);
                if (!ReferenceEquals(sanitized, property.Value)) obj[property.Key] = sanitized;
            }
        if (node is JsonArray array)
            for (int i = 0; i < array.Count; i++)
            {
                var sanitized = Sanitize(array[i]);
                if (!ReferenceEquals(sanitized, array[i])) array[i] = sanitized;
            }
        return node;
    }
}

internal sealed class RunReport
{
    public int SchemaVersion { get; } = 1;
    public string BudgetId { get; } = BudgetLedger.BudgetId;
    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
    public string ConfigurationFingerprint { get; init; } = "";
    public string Scope { get; set; } = "Synthetic API interface probe; no world actions executed.";
    public string Status { get; set; } = "started";
    public string? FailureCode { get; set; }
    public int? BalanceHttpStatus { get; set; }
    public string? Currency { get; set; }
    public bool? AccountAvailable { get; set; }
    public string? LedgerDiagnostic { get; set; }
    public List<AttemptEvidence> Attempts { get; } = [];
    public LedgerDocument? Ledger { get; set; }
}

internal sealed class AttemptEvidence
{
    public string AttemptId { get; init; } = "";
    public string ResidentId { get; init; } = "";
    public string Status { get; set; } = "started";
    public int? HttpStatus { get; set; }
    public DateTimeOffset? ArrivedAtUtc { get; set; }
    public DateTimeOffset PreSendUtc { get; init; }
    public DateTimeOffset? DispatchStartedAtUtc { get; set; }
    public long? DurationMilliseconds { get; set; }
    public string RequestFingerprint { get; init; } = "";
    public string ConfigurationFingerprint { get; init; } = "";
    public string? CandidateBody { get; set; }
    public object? ReportedUsage { get; set; }
    public string? ProviderErrorCode { get; set; }
    public string? ProviderErrorType { get; set; }
    public string? ProviderRequestId { get; set; }
    public string? ReturnedModel { get; set; }
    public string? SystemFingerprint { get; set; }
    public string? FinishReason { get; set; }
    public long? PromptTokens { get; set; }
    public long? CompletionTokens { get; set; }
    public long? ConservativeCostMicros { get; set; }
    public JsonElement? Candidate { get; set; }
    public string? FailureCode { get; set; }
}
