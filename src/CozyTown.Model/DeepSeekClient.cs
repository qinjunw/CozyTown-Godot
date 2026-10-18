using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CozyTown.Model;

// Transport and validation adapted from the a672eb5 interface probe; no world state is retained here.
public sealed class DeepSeekClient : IDisposable
{
    public const string RequestedModel = "deepseek-flash";
    private const int ResponseLimit = 1024 * 1024;
    private static readonly string[] TerminalReasons =
        ["stop", "length", "content_filter", "tool_calls", "insufficient_system_resource", "aborted"];
    private readonly PersistentBudgetLedger _ledger;
    private readonly IModelJournal _journal;
    private readonly HttpClient _http;
    private readonly string _key;
    private readonly string _expectedFingerprint;
    private readonly string _executionPurpose;
    private readonly SemaphoreSlim _accountGate = new(1);
    private bool _accountVerified;

    public DeepSeekClient(DeepSeekOptions options, IModelJournal journal) : this(options, journal, null) { }

    internal DeepSeekClient(DeepSeekOptions options, IModelJournal journal, HttpMessageHandler? testHandler)
    {
        ArgumentNullException.ThrowIfNull(options);
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        if (string.IsNullOrWhiteSpace(options.CredentialFile) || string.IsNullOrWhiteSpace(options.LedgerFile)
            || string.IsNullOrWhiteSpace(options.ExpectedSystemFingerprint))
            throw new ModelException("explicit_configuration_required");
        if (Path.GetFullPath(options.CredentialFile).Equals(Path.GetFullPath(options.LedgerFile), StringComparison.OrdinalIgnoreCase))
            throw new ModelException("credential_ledger_path_conflict");
        _ledger = PersistentBudgetLedger.OpenExisting(options.LedgerFile, options.BudgetProfileVersion);
        _key = CredentialReader.Read(options.CredentialFile);
        _expectedFingerprint = options.ExpectedSystemFingerprint;
        if (options.ExecutionPurpose is not ("legacy" or "experiment" or "manual")
            || options.BudgetProfileVersion == ModelBudgetProfile.FirstSliceVersion && options.ExecutionPurpose != "legacy"
            || options.BudgetProfileVersion == ModelBudgetProfile.ManualPlayVersion && options.ExecutionPurpose != "manual")
            throw new ModelException("execution_purpose_invalid");
        _executionPurpose = options.ExecutionPurpose;
        _http = new(testHandler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None
        }) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = ResponseLimit };
    }

    public BudgetSnapshot Budget => _ledger.Snapshot();

    // Metadata notification precedes parsing, accounting and durable completion. Handlers must not mutate the world.
    public event Action<ModelDispatch, TimeSpan>? ResponseArrived;

    public async Task<ModelCompletion> SendAsync(FrozenModelRequest request, CancellationToken cancellationToken = default)
    {
        var logicalWait = Stopwatch.StartNew();
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (_ledger.Snapshot().StopReason is string stopReason) throw new ModelException("ledger_stopped", stopReason);
        using (var admissionDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            admissionDeadline.CancelAfter(request.RemainingWait);
            await VerifyAccountAsync(admissionDeadline.Token).ConfigureAwait(false);
        }
        TimeSpan timeout = Remaining(request, logicalWait.Elapsed);
        var attempt = _ledger.Reserve(request.Stage, request.Identity.LogicalRequestId, request.IsRecovery,
            admission => _journal.WriteBudgetAdmissionAsync(admission, CancellationToken.None).GetAwaiter().GetResult());
        var dispatch = new ModelDispatch(request, attempt.AttemptId,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.InputJson))).ToLowerInvariant(),
            DateTimeOffset.UtcNow, timeout, _ledger.Snapshot());
        DateTimeOffset? sentAt = null, arrivedAt = null;
        TimeSpan? logicalElapsedToArrival = null;
        int? httpStatus = null;
        string? providerId = null, model = null, fingerprint = null, finish = null, candidate = null, failure = null, warning = null;
        ModelUsage? usage = null;
        bool ended = false, settled = false;
        var duration = new Stopwatch();
        try
        {
            try { await _journal.WriteRequestAsync(dispatch, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (ModelException error) when (error.Code is "host_request_retired_before_send" or "host_request_terminal_before_send"
                or "host_unavailable_before_send" or "logical_wait_expired") { throw; }
            catch { throw new ModelException("request_journal_failed"); }
            cancellationToken.ThrowIfCancellationRequested();
            timeout = Remaining(request, logicalWait.Elapsed);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            using var message = NewMessage(HttpMethod.Post, "chat/completions");
            message.Content = new StringContent(request.InputJson, Encoding.UTF8, "application/json");
            sentAt = DateTimeOffset.UtcNow;
            duration.Start();
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            httpStatus = (int)response.StatusCode;
            string body = await ReadBodyAsync(response, deadline.Token).ConfigureAwait(false);
            arrivedAt = DateTimeOffset.UtcNow;
            logicalElapsedToArrival = logicalWait.Elapsed;
            duration.Stop();
            NotifyArrival(dispatch, logicalElapsedToArrival.Value);
            var parsed = ParseResponse(body, httpStatus.Value);
            await _journal.WriteResponseArrivalAsync(dispatch, ProjectResponse(parsed, httpStatus.Value),
                logicalElapsedToArrival.Value, CancellationToken.None).ConfigureAwait(false);
            providerId = parsed.Id; model = parsed.Model; fingerprint = parsed.Fingerprint;
            finish = parsed.Finish; candidate = parsed.Candidate; usage = parsed.Usage; ended = parsed.GenerationEnded;
            if (usage is not null && fingerprint != _expectedFingerprint) warning = "fingerprint_changed";
            if (!response.IsSuccessStatusCode)
            {
                failure = parsed.FailureCode!;
                _ledger.MarkUnknown(attempt.AttemptId, failure, false,
                    httpStatus is 401 or 403 or 402);
            }
            else
            {
                if (usage is null) throw new ModelException(parsed.FailureCode ?? "usage_invalid_or_missing");
                _ledger.Settle(attempt.AttemptId, usage.PromptTokens, usage.CompletionTokens);
                settled = true;
                if (duration.Elapsed >= timeout) failure = "response_late";
                else failure = parsed.FailureCode;
            }
        }
        catch (Exception error)
        {
            duration.Stop();
            failure = ErrorCode(error);
            try
            {
                if (sentAt is null)
                {
                    // Reservation succeeded but SendAsync was never invoked: no provider generation occurred.
                    _ledger.Settle(attempt.AttemptId, 0, 0);
                    settled = true;
                    if (failure == "request_journal_failed") _ledger.Stop(failure);
                }
                else if (!settled)
                    _ledger.MarkUnknown(attempt.AttemptId, failure, ended,
                        arrivedAt != null || httpStatus is 401 or 403 or 402
                        || failure is "model_not_registered" or "fingerprint_not_registered");
            }
            catch { failure = "ledger_update_failed"; }
        }

        BudgetAttempt? finalAttempt = null;
        BudgetSnapshot? finalBudget = null;
        try { finalBudget = _ledger.Snapshot(); finalAttempt = finalBudget.Attempts.Single(a => a.AttemptId == attempt.AttemptId); }
        catch { failure = "ledger_read_failed"; }
        // Never export malformed/partial candidate text or unbounded provider metadata.
        candidate = SafeCandidate(candidate);
        providerId = SafeMetadata(providerId);
        model = SafeMetadata(model);
        fingerprint = SafeMetadata(fingerprint);
        finish = SafeMetadata(finish);
        var completion = new ModelCompletion(dispatch, sentAt, arrivedAt, duration.Elapsed,
            httpStatus, providerId, model, fingerprint, finish, candidate, usage, failure,
            ended, settled, finalAttempt?.SlotHeld ?? true, settled ? finalAttempt?.SettledCostMicros : null, logicalElapsedToArrival, finalBudget,
            warning, SafeMetadata(_expectedFingerprint), _executionPurpose, arrivedAt is null ? null : 1);
        try { await _journal.WriteCompletionAsync(completion, CancellationToken.None).ConfigureAwait(false); }
        catch
        {
            try { _ledger.Stop("completion_journal_failed"); } catch { }
            completion = completion with { FailureCode = "completion_journal_failed" };
        }
        return completion;
    }

    private void NotifyArrival(ModelDispatch dispatch, TimeSpan elapsed)
    {
        var observers = ResponseArrived;
        if (observers is null) return;
        foreach (Action<ModelDispatch, TimeSpan> observer in observers.GetInvocationList())
        {
            try { observer(dispatch, elapsed); }
            // Diagnostic subscribers cannot prevent accounting or persistence of the provider result.
            catch { }
        }
    }

    public async Task VerifyAccountAsync(CancellationToken cancellationToken = default)
    {
        await _accountGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_accountVerified) return;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            using var message = NewMessage(HttpMethod.Get, "user/balance");
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.PaymentRequired)
            {
                _ledger.Stop("account_check_rejected");
                throw new ModelException("account_check_rejected");
            }
            if (!response.IsSuccessStatusCode) throw new ModelException("account_check_http_failed");
            using var document = JsonDocument.Parse(await ReadBodyAsync(response, deadline.Token).ConfigureAwait(false));
            var root = document.RootElement;
            JsonData.RequireUniqueProperties(root);
            if (!root.TryGetProperty("is_available", out var available) || available.ValueKind != JsonValueKind.True)
                throw new ModelException("account_unavailable");
            if (!root.TryGetProperty("balance_infos", out var infos) || infos.ValueKind != JsonValueKind.Array
                || infos.GetArrayLength() != 1 || !infos[0].TryGetProperty("currency", out var currency)
                || currency.ValueKind != JsonValueKind.String || currency.GetString() != "CNY")
                throw new ModelException("account_currency_unverified");
            _accountVerified = true;
        }
        catch (Exception error)
        {
            if (error is ModelException { Code: "account_unavailable" or "account_currency_unverified" } known)
                _ledger.Stop(known.Code);
            throw new ModelException(ErrorCode(error));
        }
        finally { _accountGate.Release(); }
    }

    private HttpRequestMessage NewMessage(HttpMethod method, string relativePath)
    {
        var message = new HttpRequestMessage(method, new Uri("https://api.deepseek.com/" + relativePath));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        return message;
    }

    private static TimeSpan Remaining(FrozenModelRequest request, TimeSpan elapsed)
    {
        var remaining = request.RemainingWait - elapsed;
        if (remaining <= TimeSpan.Zero) throw new ModelException("logical_wait_expired");
        return remaining < TimeSpan.FromSeconds(30) ? remaining : TimeSpan.FromSeconds(30);
    }

    private void ValidateRequest(FrozenModelRequest request)
    {
        if (_executionPurpose == "manual" && request?.Stage != ModelStage.Manual)
            throw new ModelException("execution_stage_mismatch");
        if (request is not null && !_ledger.Profile.StageLimits.ContainsKey(ModelBudgetProfile.StageName(request.Stage)))
            throw new ModelException("stage_invalid");
        if (request?.Identity is not { } identity || new[] { identity.RunId, identity.SessionId,
                identity.LogicalRequestId, identity.ResidentId }.Any(string.IsNullOrWhiteSpace)
            || identity.ActivityRevision < 0 || identity.Kind is not ("npc_action" or "player_text")
            || (identity.Kind == "player_text" ? string.IsNullOrWhiteSpace(identity.PlayerSessionId) : identity.PlayerSessionId is not null)
            || request.RemainingWait <= TimeSpan.Zero
            || request.RemainingWait > TimeSpan.FromSeconds(60)) throw new ModelException("request_identity_invalid");
        if (string.IsNullOrWhiteSpace(request.InputJson) || Encoding.UTF8.GetByteCount(request.InputJson) > 16 * 1024)
            throw new ModelException("request_size_invalid");
        try
        {
            using var document = JsonDocument.Parse(request.InputJson);
            var root = document.RootElement;
            JsonData.RequireUniqueProperties(root);
            string[] allowed = ["model", "thinking", "stream", "max_tokens", "response_format", "messages"];
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p => !allowed.Contains(p.Name))
                || root.GetProperty("model").GetString() != RequestedModel
                || root.GetProperty("thinking").GetProperty("type").GetString() != "disabled"
                || root.GetProperty("stream").GetBoolean()
                || root.GetProperty("max_tokens").GetInt32() != 512
                || root.GetProperty("response_format").GetProperty("type").GetString() != "json_object")
                throw new ModelException("request_configuration_invalid");
            var messages = root.GetProperty("messages");
            if (messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() == 0)
                throw new ModelException("request_messages_invalid");
            bool jsonInstruction = false;
            foreach (var message in messages.EnumerateArray())
            {
                string? role = message.GetProperty("role").GetString();
                if (role is not ("system" or "user" or "assistant")
                    || message.EnumerateObject().Any(p => p.Name is not ("role" or "content")))
                    throw new ModelException("request_messages_invalid");
                string content = message.GetProperty("content").GetString() ?? throw new ModelException("request_messages_invalid");
                if (role is "system" or "user") jsonInstruction |= content.Contains("json", StringComparison.OrdinalIgnoreCase);
            }
            if (!jsonInstruction) throw new ModelException("json_instruction_missing");
            if (ContainsSecret(request.InputJson) || ContainsSecret(JsonSerializer.Serialize(identity)))
                throw new ModelException("request_contains_credential");
        }
        catch (ModelException) { throw; }
        catch { throw new ModelException("request_json_invalid"); }
    }

    private static void ParseGeneration(JsonElement root, out string id, out string model,
        out string? fingerprint, out string finish, out string? content)
    {
        try
        {
            id = root.GetProperty("id").GetString()!;
            model = root.GetProperty("model").GetString()!;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(model)
                || root.GetProperty("object").GetString() != "chat.completion") throw new JsonException();
            var choices = root.GetProperty("choices");
            if (choices.GetArrayLength() != 1) throw new JsonException();
            var choice = choices[0];
            finish = choice.GetProperty("finish_reason").GetString()!;
            if (choice.GetProperty("index").GetInt32() != 0 || !TerminalReasons.Contains(finish)) throw new JsonException();
            var message = choice.GetProperty("message");
            if (message.GetProperty("role").GetString() != "assistant") throw new JsonException();
            content = message.TryGetProperty("content", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
            fingerprint = root.TryGetProperty("system_fingerprint", out var fp) && fp.ValueKind == JsonValueKind.String ? fp.GetString() : null;
        }
        catch { throw new ModelException("generation_terminal_unverified"); }
    }

    private static ModelUsage ParseUsage(JsonElement root)
    {
        try
        {
            var usage = root.GetProperty("usage");
            long p = usage.GetProperty("prompt_tokens").GetInt64(), c = usage.GetProperty("completion_tokens").GetInt64();
            long total = usage.GetProperty("total_tokens").GetInt64();
            long hit = usage.GetProperty("prompt_cache_hit_tokens").GetInt64(), miss = usage.GetProperty("prompt_cache_miss_tokens").GetInt64();
            _ = PersistentBudgetLedger.CalculateCost(p, c);
            if (hit < 0 || miss < 0 || hit > p || miss > p || hit + miss != p || total != p + c)
                throw new JsonException();
            return new(p, c, total, hit, miss);
        }
        catch { throw new ModelException("usage_invalid_or_missing"); }
    }

    private sealed record ParsedResponse(string? Id, string? Model, string? Fingerprint, string? Finish,
        string? Candidate, ModelUsage? Usage, string? FailureCode, bool GenerationEnded);

    // Validate the original document, including properties that will not be persisted.
    private ParsedResponse ParseResponse(string body, int status)
    {
        string? id = null, model = null, fingerprint = null, finish = null, candidate = null, failure = null;
        ModelUsage? usage = null;
        bool generationEnded = false;
        try
        {
            if (status is < 200 or >= 300)
                throw new ModelException(status switch
                {
                    401 or 403 => "authentication_failed", 402 => "account_unavailable",
                    429 => "rate_limited", _ => "provider_http_failure"
                });
            using var document = JsonDocument.Parse(body);
            JsonData.RequireUniqueProperties(document.RootElement);
            ParseGeneration(document.RootElement, out id, out model, out fingerprint, out finish, out candidate);
            generationEnded = true;
            if (ContainsSecret(id) || ContainsSecret(model) || ContainsSecret(fingerprint) || ContainsSecret(candidate))
            {
                id = model = fingerprint = candidate = null;
                throw new ModelException("response_secret_echo");
            }
            if (model != RequestedModel) throw new ModelException("model_not_registered");
            if (string.IsNullOrWhiteSpace(fingerprint) || SafeMetadata(fingerprint) is null
                || fingerprint != _expectedFingerprint && _executionPurpose != "manual")
                throw new ModelException("fingerprint_not_registered");
            usage = ParseUsage(document.RootElement);
            if (finish != "stop") throw new ModelException("generation_" + finish);
            if (string.IsNullOrWhiteSpace(candidate)) throw new ModelException("candidate_empty");
            using var parsed = JsonDocument.Parse(candidate);
            JsonData.RequireUniqueProperties(parsed.RootElement);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new ModelException("candidate_not_object");
        }
        catch (Exception error) { failure = ErrorCode(error); }
        return new(id, model, fingerprint, finish, candidate, usage, failure, generationEnded);
    }

    // The projection retains validation facts and excludes reasoning, unused fields and HTTP error bodies.
    private string ProjectResponse(ParsedResponse response, int status)
    {
        var usage = response.Usage;
        return JsonSerializer.Serialize(new
        {
            projectionVersion = 1, httpStatus = status, validationFailure = response.FailureCode, generationEnded = response.GenerationEnded,
            id = SafeMetadata(response.Id), model = SafeMetadata(response.Model), system_fingerprint = SafeMetadata(response.Fingerprint),
            choices = new[] { new { index = 0, finish_reason = SafeMetadata(response.Finish), message = new { role = "assistant", content = SafeCandidate(response.Candidate) } } },
            usage = usage is null ? null : new { prompt_tokens = usage.PromptTokens, completion_tokens = usage.CompletionTokens,
                total_tokens = usage.TotalTokens, prompt_cache_hit_tokens = usage.CacheHitTokens, prompt_cache_miss_tokens = usage.CacheMissTokens },
            executionPurpose = _executionPurpose, expectedSystemFingerprint = SafeMetadata(_expectedFingerprint)
        });
    }

    private bool ContainsSecret(string? text, int depth = 0)
    {
        if (text is null) return false;
        if (text.Contains(_key, StringComparison.Ordinal)) return true;
        if (depth >= 8) return true;
        try
        {
            using var parsed = JsonDocument.Parse(text);
            return Search(parsed.RootElement);
        }
        catch (JsonException) { return false; }
        bool Search(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => ContainsSecret(value.GetString(), depth + 1),
            JsonValueKind.Array => value.EnumerateArray().Any(Search),
            JsonValueKind.Object => value.EnumerateObject().Any(p => ContainsSecret(p.Name, depth + 1) || Search(p.Value)),
            _ => false
        };
    }

    private string? SafeCandidate(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        try
        {
            using var parsed = JsonDocument.Parse(candidate);
            JsonData.RequireUniqueProperties(parsed.RootElement);
            return parsed.RootElement.ValueKind == JsonValueKind.Object && !ContainsSecret(candidate) ? candidate : null;
        }
        catch { return null; }
    }

    private string? SafeMetadata(string? text) => text is not null && text.Length <= 256
        && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') && !ContainsSecret(text) ? text : null;

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > ResponseLimit) throw new ModelException("response_too_large");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > ResponseLimit) throw new ModelException("response_too_large");
            output.Write(buffer, 0, count);
        }
        return new UTF8Encoding(false, true).GetString(output.ToArray());
    }

    private static string ErrorCode(Exception error) => error switch
    {
        ModelException known => known.Code, OperationCanceledException => "transport_timeout_or_cancelled",
        HttpRequestException => "transport_failure", JsonException => "invalid_json",
        IOException => "file_io_failure", _ => "unexpected_failure"
    };

    public void Dispose() { _http.Dispose(); _accountGate.Dispose(); }
}
