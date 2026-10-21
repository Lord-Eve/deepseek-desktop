using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeepSeekDesktop.Core;
using DeepSeekDesktop.Gateway;

int checks = 0;
void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
int Port() { var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); int port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop(); return port; }
Account FixtureAccount() => new() { Provider = Providers.Grok, Name = "模拟 Grok", AccessToken = "fixture-grok-access-original", RefreshToken = "fixture-grok-refresh-original", TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() };
Settings FixtureSettings(int quota = 50) => new() { Provider = Providers.Grok, Port = Port(), HourlyQuota = quota, Accounts = [FixtureAccount()] };
async Task Reject(Func<Task> action, int status, string message)
{
    bool rejected = false; try { await action(); } catch (UpstreamException ex) when (ex.Status == status) { rejected = true; } Check(rejected, message);
}
void BadRequest(Action action, string message)
{
    bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Check(rejected, message);
}
Check(GrokOAuth.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk") == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", "PKCE S256 must match the RFC 7636 test vector.");
var authHandler = new FakeGrok(); using var authHttp = new HttpClient(authHandler) { Timeout = TimeSpan.FromSeconds(10) };
int callbackPort = Port(); var oauth = new GrokOAuth(authHttp, callbackPort);
var attempt = await oauth.AuthorizeAsync(default); var second = await oauth.AuthorizeAsync(default);
Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]), StringComparer.Ordinal);
var query = Query(attempt.Url);
Check(attempt.Url.Host == "auth.x.ai" && query["code_challenge_method"] == "S256" && query["code_challenge"] == GrokOAuth.Challenge(attempt.Verifier) && attempt.State != second.State && attempt.Verifier != second.Verifier, "Every login must use the official origin and fresh state/verifier.");
using (var maliciousHttp = new HttpClient(new FakeGrok { EvilDiscovery = true }))
    await Reject(async () => { await new GrokOAuth(maliciousHttp).AuthorizeAsync(default); }, 502, "OAuth discovery must never send authorization codes or tokens to another origin.");
bool wrongStateRejected = false;
var login = await oauth.LoginAsync(async url =>
{
    var values = Query(url);
    using var local = new HttpClient(new HttpClientHandler { UseProxy = false });
    using var wrong = await local.GetAsync($"http://127.0.0.1:{callbackPort}/callback?state=wrong&code=fixture-code");
    wrongStateRejected = (int)wrong.StatusCode == 400;
    using var right = await local.GetAsync($"http://127.0.0.1:{callbackPort}/callback?state={values["state"]}&code=fixture-code");
    Check(right.IsSuccessStatusCode && right.Headers.CacheControl?.NoStore == true, "Valid callbacks must not cache authentication data.");
    authHandler.ExpectedChallenge = values["code_challenge"];
}, default);
Check(wrongStateRejected && authHandler.CodeExchanges == 1 && authHandler.VerifierMatched && login.RefreshToken == "fixture-grok-refresh-rotated", "Wrong-state callbacks must not exchange a code; the valid code must use the original verifier.");
var occupiedCallback = new TcpListener(IPAddress.Loopback, callbackPort); occupiedCallback.Start(); bool browserOpened = false, occupiedRejected = false;
try { await oauth.LoginAsync(_ => { browserOpened = true; return Task.CompletedTask; }, default); } catch (IOException) { occupiedRejected = true; } finally { occupiedCallback.Stop(); }
Check(occupiedRejected && !browserOpened && authHandler.CodeExchanges == 1, "An occupied callback port must fail before opening the browser or consuming an authorization code.");
using (var cancel = new CancellationTokenSource())
{
    bool canceled = false;
    try { await oauth.LoginAsync(_ => { cancel.Cancel(); return Task.CompletedTask; }, cancel.Token); } catch (OperationCanceledException) { canceled = true; }
    var reuse = new TcpListener(IPAddress.Loopback, callbackPort); reuse.Start(); reuse.Stop();
    Check(canceled && authHandler.CodeExchanges == 1, "Canceling login must release the listener and leave old credentials alone.");
}
GrokLoginInput? manualInput = null;
int exchangesBeforeManual = authHandler.CodeExchanges;
await oauth.LoginAsync(async url =>
{
    var values = Query(url); authHandler.ExpectedChallenge = values["code_challenge"];
    Check(manualInput!.CanSubmit && !manualInput.SubmitCode("  ") && !manualInput.SubmitCode("invalid code"), "Manual entry must reject empty or malformed codes without ending the pending login.");
    Check(manualInput.SubmitCode(" fixture-manual-code ") && !manualInput.SubmitCode("fixture-duplicate"), "Manual entry must accept a code once and reject duplicate submissions.");
    using var local = new HttpClient(new HttpClientHandler { UseProxy = false });
    using var late = await local.GetAsync($"http://127.0.0.1:{callbackPort}/callback?state={values["state"]}&code=fixture-late-callback");
}, default, input => manualInput = input);
Check(authHandler.CodeExchanges == exchangesBeforeManual + 1 && authHandler.VerifierMatched && !manualInput!.CanSubmit && !manualInput.SubmitCode("fixture-stale"), "Manual code and a competing callback must exchange only once with the original PKCE verifier; a finished input must be invalidated.");
using (var cancel = new CancellationTokenSource())
{
    bool canceled = false;
    try { await oauth.LoginAsync(_ => { cancel.Cancel(); return Task.CompletedTask; }, cancel.Token, input => manualInput = input); } catch (OperationCanceledException) { canceled = true; }
    Check(canceled && !manualInput!.SubmitCode("fixture-after-cancel") && authHandler.CodeExchanges == exchangesBeforeManual + 1, "Canceled login input must never accept or exchange a later code.");
}
var refreshAccount = FixtureAccount(); refreshAccount.TokenExpiresAt = 0;
var refreshHandler = new FakeGrok(); using var refreshHttp = new HttpClient(refreshHandler);
int saves = 0; var refreshClient = new GrokClient(refreshHttp, refreshAccount, (id, tokens) => { saves++; Check(id == refreshAccount.Id && tokens.AccessToken == "fixture-grok-access-rotated", "Token persistence must identify the correct account."); });
await Task.WhenAll(refreshClient.EnsureAccessAsync(default), refreshClient.EnsureAccessAsync(default));
Check(refreshHandler.Refreshes == 1 && saves == 1 && refreshAccount.RefreshToken == "fixture-grok-refresh-rotated", "Concurrent access must refresh once and retain a rotated refresh token.");
var failedSaveHandler = new FakeGrok(); var failedSaveAccount = FixtureAccount(); failedSaveAccount.TokenExpiresAt = 0;
using (var saveHttp = new HttpClient(failedSaveHandler))
{
    int attempts = 0; var saving = new GrokClient(saveHttp, failedSaveAccount, (_, _) => { if (++attempts == 1) throw new IOException("fixture save failure"); });
    await Reject(() => saving.EnsureAccessAsync(default), 503, "A failed token save must stop the request rather than silently use unsaved credentials.");
    await saving.EnsureAccessAsync(default);
    Check(attempts == 2 && failedSaveHandler.Refreshes == 1, "Retrying after a save failure must persist the same rotated credentials without another refresh.");
}
var limitedRefresh = new FakeGrok { RefreshRateLimited = true }; var oldAccount = FixtureAccount(); oldAccount.TokenExpiresAt = 0;
using (var limitedHttp = new HttpClient(limitedRefresh))
{
    await Reject(() => new GrokClient(limitedHttp, oldAccount).EnsureAccessAsync(default), 429, "A transient refresh rate limit must be reported without deleting the login.");
    Check(oldAccount.RefreshToken == "fixture-grok-refresh-original", "A failed refresh must preserve the previous refresh token.");
}

const string toolsJson = """[{"type":"function","function":{"name":"mcp__exa__web_search_exa","description":"模拟 Exa 搜索","parameters":{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}}}]""";
var toolDefinitions = JsonNode.Parse(toolsJson)!;
JsonArray InitialMessages(string question = "请用 Exa 搜索鲸鱼资料") => new(new JsonObject { ["role"] = "system", ["content"] = "使用搜索结果回答" }, new JsonObject { ["role"] = "user", ["content"] = question });
JsonObject ChatRequest(bool stream = false, JsonArray? messages = null) => new() { ["model"] = "grok-build", ["messages"] = messages ?? InitialMessages(), ["tools"] = toolDefinitions.DeepClone(), ["stream"] = stream };
JsonElement Element(JsonNode value) => JsonSerializer.SerializeToElement(value);
var replay = new GrokReplayCache();
var prepared = GrokProtocol.Chat(Element(ChatRequest()), replay);
Check(prepared["instructions"]?.ToString() == "使用搜索结果回答" && prepared["input"]!.AsArray().Count == 1 && prepared["tools"]![0]!["name"]?.ToString() == "mcp__exa__web_search_exa" && prepared["tools"]![0]!["function"] == null, "Chat messages and nested tools must be converted to native Responses without prompt simulation.");
Check(prepared["store"]!.GetValue<bool>() == false && prepared["include"]!.AsArray().Any(v => v?.ToString() == "reasoning.encrypted_content"), "The proxy request must ask for encrypted reasoning and disable remote storage.");
var forRewrite = JsonNode.Parse("""{"model":"grok-build","input":[{"role":"developer","content":[{"type":"input_text","text":"规则"}]},{"type":"reasoning","summary":[]},{"role":"user","content":"hello"}],"reasoning":{"effort":"high"},"prompt_cache_retention":"24h"}""")!;
var nativeRewrite = GrokProtocol.Native(Element(forRewrite));
Check(nativeRewrite["instructions"]?.ToString() == "规则" && nativeRewrite["input"]!.AsArray().Count == 1 && nativeRewrite["reasoning"] == null && nativeRewrite["prompt_cache_retention"] == null, "Native Responses must hoist instructions, drop unsigned reasoning and omit unsupported legacy knobs.");
var completion = GrokProtocol.Complete(prepared, FakeGrok.CallResponse()); replay.Save(prepared, completion);
JsonObject ReturnedMessage(GrokCompletion value) => new() { ["role"] = "assistant", ["content"] = null, ["tool_calls"] = new JsonArray(value.Calls.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments } }).ToArray()) };
var history = InitialMessages(); history.Add(ReturnedMessage(completion)); history.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = completion.Calls[0].Id, ["content"] = "模拟 Exa：鲸鱼是哺乳动物。" });
var continuation = GrokProtocol.Chat(Element(ChatRequest(messages: history)), replay);
Check(continuation["input"]!.AsArray().Any(t => t?["encrypted_content"]?.ToString() == "fixture-encrypted-reasoning") && continuation["input"]!.AsArray().Last()?["type"]?.ToString() == "function_call_output", "Tool results must replay the original encrypted reasoning and native function-call items.");
BadRequest(() => GrokProtocol.Chat(Element(ChatRequest(messages: (JsonArray)history.DeepClone())), new GrokReplayCache()), "A restarted or expired tool context must return a clear error instead of silently dropping reasoning.");
var changedHistory = (JsonArray)history.DeepClone(); changedHistory[1]!["content"] = "改写提问";
BadRequest(() => GrokProtocol.Chat(Element(ChatRequest(messages: changedHistory)), replay), "Tool replay must be bound to the actual model and conversation prefix.");
var malformed = FakeGrok.CallResponse(); malformed["output"]![1]!["arguments"] = "{bad";
await Reject(() => { GrokProtocol.Complete(prepared, malformed); return Task.CompletedTask; }, 502, "Malformed upstream function arguments must not become executable events.");
var undeclared = FakeGrok.CallResponse(); undeclared["output"]![1]!["name"] = "invented_tool";
await Reject(() => { GrokProtocol.Complete(prepared, undeclared); return Task.CompletedTask; }, 502, "The model must not call an undeclared tool.");

var settings = FixtureSettings(); var handler = new FakeGrok(); var logs = new List<string>();
await using var gateway = new GrokGateway(settings, log: logs.Add, handler: handler); await gateway.StartAsync();
Check(handler.Requests == 0 && handler.Refreshes == 0, "Startup with an unexpired login must send no model or quota request.");
using var api = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(settings.BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(10) };
using (var denied = await api.GetAsync("models")) Check(denied.StatusCode == HttpStatusCode.Unauthorized, "Grok local endpoints must require the local API key.");
api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
using (var request = new HttpRequestMessage(HttpMethod.Get, "models")) { request.Headers.Add("Origin", "https://untrusted.example"); using var denied = await api.SendAsync(request); Check(denied.StatusCode == HttpStatusCode.Forbidden, "Grok local API must reject browser origins."); }
using (var models = await api.GetAsync("models")) Check(models.IsSuccessStatusCode && (await models.Content.ReadAsStringAsync()).Contains("grok-build"), "Preset model listing must work without invoking the upstream model.");
using (var admin = await api.GetAsync("../admin/api/status")) Check(admin.StatusCode == HttpStatusCode.NotFound, "Grok admin state must not be exposed through the public API.");
async Task<HttpResponseMessage> Send(JsonObject json, string path = "chat/completions") => await api.PostAsync(path, new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json"));
JsonObject firstMessage;
using (var response = await Send(ChatRequest()))
{
    var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    firstMessage = json["choices"]![0]!["message"]!.AsObject();
    Check(response.IsSuccessStatusCode && json["choices"]![0]!["finish_reason"]?.ToString() == "tool_calls" && firstMessage["content"] == null && firstMessage["tool_calls"]![0]!["function"]!["name"]?.ToString() == "mcp__exa__web_search_exa", "Non-streaming responses must expose real function_calls as OpenAI tool_calls.");
    Check(!json.ToJsonString().Contains("fixture-encrypted-reasoning"), "Encrypted reasoning must never leak into Chat Completions text or metadata.");
}
Check(handler.LastHeaders.ContainsKey("x-grok-client-version") && handler.LastHeaders["x-grok-client-identifier"] == "deepseek-desktop" && handler.LastHeaders["x-xai-token-auth"] == "xai-grok-cli", "Proxy requests must carry the expected client markers without impersonating Cherry Studio.");
int exaExecutions = 0; string MockExa() { exaExecutions++; return "模拟 Exa：鲸鱼是哺乳动物。来源 https://fixture.example/exa"; }
var actualHistory = InitialMessages(); actualHistory.Add(firstMessage.DeepClone()); actualHistory.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = firstMessage["tool_calls"]![0]!["id"]!.ToString(), ["content"] = MockExa() });
using (var response = await Send(ChatRequest(messages: actualHistory)))
{
    var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    Check(response.IsSuccessStatusCode && json["choices"]![0]!["finish_reason"]?.ToString() == "stop" && json["choices"]![0]!["message"]!["content"]!.ToString().Contains("哺乳动物") && handler.LastBody.Contains("fixture-encrypted-reasoning") && handler.LastBody.Contains("https://fixture.example/exa") && exaExecutions == 1, "The mock Exa loop must execute on the client and round-trip through native reasoning + tool output.");
    Check(json["usage"]?["total_tokens"]?.GetValue<int>() == 15, "Usage must be converted from actual upstream counters, never estimated.");
}
using (var response = await Send(ChatRequest(stream: true)))
{
    string body = await response.Content.ReadAsStringAsync();
    Check(response.IsSuccessStatusCode && body.Contains("\"tool_calls\"") && body.Contains("\"index\":0") && body.Contains("\"finish_reason\":\"tool_calls\"") && body.EndsWith("data: [DONE]\n\n", StringComparison.Ordinal) && !body.Contains("fixture-encrypted-reasoning"), "Streaming tool events must be indexed and finalized without exposing encrypted context.");
}
handler.Mode = "text";
using (var response = await Send(ChatRequest(stream: true)))
{
    string body = await response.Content.ReadAsStringAsync();
    Check(body.Contains("根据模拟") || body.Contains("\\u6839"), "Text deltas must appear on the Chat stream.");
    Check(response.IsSuccessStatusCode && body.Contains("\"finish_reason\":\"stop\"") && body.EndsWith("data: [DONE]\n\n", StringComparison.Ordinal), "Text SSE must finish normally.");
}
var nativeRequest = JsonNode.Parse("""{"model":"grok-build","input":"你好","stream":false}""")!.AsObject();
using (var response = await Send(nativeRequest, "responses")) { var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!; Check(response.IsSuccessStatusCode && json["object"]?.ToString() == "response" && json["output"] is JsonArray, "Native non-stream Responses must return the upstream response object."); }
nativeRequest["stream"] = true;
using (var response = await Send(nativeRequest, "responses")) { string body = await response.Content.ReadAsStringAsync(); Check(response.IsSuccessStatusCode && body.Contains("response.completed") && !body.Contains("chat.completion.chunk"), "Native SSE must preserve Responses event types."); }
handler.Mode = "truncated";
using (var response = await Send(ChatRequest(stream: true))) { string body = await response.Content.ReadAsStringAsync(); Check((int)response.StatusCode == 502 && !body.Contains("\"tool_calls\"") && !body.Contains("[DONE]"), "Truncated tool output must never be emitted as an executable call or successful completion."); }
handler.Mode = "rate";
using (var response = await Send(ChatRequest())) Check((int)response.StatusCode == 429, "Upstream quota limits must remain distinguishable.");
handler.Mode = "unauthorized-once"; int beforeRetry = handler.Requests, beforeRefresh = handler.Refreshes;
using (var response = await Send(ChatRequest())) Check(response.IsSuccessStatusCode && handler.Requests == beforeRetry + 2 && handler.Refreshes == beforeRefresh + 1, "An HTTP 401 before streaming may refresh and retry once, but not replay a partially emitted response.");
Check(gateway.Status("stats").GetProperty("failed_requests").GetInt32() == 2 && !string.Join('\n', logs).Contains("fixture-grok-access"), "Statistics and logs must distinguish failures without retaining credentials.");
handler.Mode = "bad-snapshot";
using (var response = await Send(ChatRequest())) Check((int)response.StatusCode == 502, "Malformed upstream completion snapshots must be classified as protocol errors, not bad client input.");
handler.Mode = "denied";
using (var response = await Send(ChatRequest())) Check((int)response.StatusCode == 401, "A rejected subscription or authorization must mark the account unavailable.");
int requestsAfterDenial = handler.Requests;
using (var response = await Send(ChatRequest())) Check((int)response.StatusCode == 503 && handler.Requests == requestsAfterDenial, "Further requests after auth denial must not keep refreshing or calling the rejected upstream.");

var limitedSettings = FixtureSettings(1); var quotaHandler = new FakeGrok { Mode = "text" };
await using (var limited = new GrokGateway(limitedSettings, handler: quotaHandler))
{
    await limited.StartAsync(); using var local = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(limitedSettings.BaseUrl + "/") }; local.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", limitedSettings.ApiKey);
    using var first = await local.PostAsync("chat/completions", new StringContent(ChatRequest().ToJsonString(), Encoding.UTF8, "application/json"));
    using var secondCall = await local.PostAsync("chat/completions", new StringContent(ChatRequest().ToJsonString(), Encoding.UTF8, "application/json"));
    Check(first.IsSuccessStatusCode && (int)secondCall.StatusCode == 429 && quotaHandler.Requests == 1, "The shared local hourly limit must prevent an extra Grok request.");
}
var cancelSettings = FixtureSettings(); var waitHandler = new FakeGrok { Mode = "wait" };
await using (var canceledGateway = new GrokGateway(cancelSettings, handler: waitHandler))
{
    await canceledGateway.StartAsync(); using var local = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(cancelSettings.BaseUrl + "/") }; local.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cancelSettings.ApiKey);
    var pending = local.PostAsync("chat/completions", new StringContent(ChatRequest().ToJsonString(), Encoding.UTF8, "application/json"));
    await waitHandler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var watch = Stopwatch.StartNew(); await canceledGateway.DisposeAsync();
    try { using var response = await pending; } catch (HttpRequestException) { }
    var reuse = new TcpListener(IPAddress.Loopback, cancelSettings.Port); reuse.Start(); reuse.Stop();
    Check(watch.Elapsed < TimeSpan.FromSeconds(6), "Stopping must cancel the upstream request, clear replay state and release the local port.");
}
Console.WriteLine($"PASS: {checks} Grok OAuth, protocol and mock Exa loop checks (no real login or model calls)");

sealed class FakeGrok : HttpMessageHandler
{
    public string Mode = "calls", ExpectedChallenge = "", LastBody = "";
    public bool EvilDiscovery, VerifierMatched, RefreshRateLimited;
    public int CodeExchanges, Refreshes, Requests;
    private bool returnedUnauthorized;
    public Dictionary<string, string> LastHeaders = new();
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static JsonObject CallResponse() => JsonNode.Parse("""{"id":"resp_fixture","object":"response","status":"completed","output":[{"type":"reasoning","id":"rs_fixture","encrypted_content":"fixture-encrypted-reasoning","summary":[]},{"type":"function_call","id":"fc_fixture","call_id":"call_fixture","name":"mcp__exa__web_search_exa","arguments":"{\"query\":\"鲸鱼资料\"}"}],"usage":{"input_tokens":10,"output_tokens":5,"total_tokens":15}}""")!.AsObject();
    private static JsonObject TextResponse() => JsonNode.Parse("""{"id":"resp_final","object":"response","status":"completed","output":[{"type":"message","id":"msg_fixture","role":"assistant","content":[{"type":"output_text","text":"根据模拟 Exa 结果，鲸鱼是哺乳动物。"}]}],"usage":{"input_tokens":10,"output_tokens":5,"total_tokens":15}}""")!.AsObject();
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        string host = request.RequestUri!.Host, path = request.RequestUri.AbsolutePath;
        if (host is not ("auth.x.ai" or "cli-chat-proxy.grok.com") || request.RequestUri.Scheme != "https") throw new Exception("Unexpected credential destination.");
        if (path.Contains("openid-configuration")) return Json(JsonSerializer.Serialize(new { authorization_endpoint = EvilDiscovery ? "http://evil.example/authorize" : "https://auth.x.ai/oauth2/authorize", token_endpoint = "https://auth.x.ai/oauth2/token" }));
        if (path == "/oauth2/token")
        {
            string body = await request.Content!.ReadAsStringAsync(cancellation);
            var fields = body.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
            if (fields["client_id"] != GrokOAuth.ClientId) throw new Exception("Unexpected OAuth client ID.");
            if (fields["grant_type"] == "authorization_code") { CodeExchanges++; VerifierMatched = GrokOAuth.Challenge(fields["code_verifier"]) == ExpectedChallenge; }
            else { Refreshes++; if (RefreshRateLimited) return new(HttpStatusCode.TooManyRequests); }
            return Json("""{"access_token":"fixture-grok-access-rotated","refresh_token":"fixture-grok-refresh-rotated","expires_in":3600}""");
        }
        if (host != "cli-chat-proxy.grok.com" || path != "/v1/responses") throw new Exception("Unexpected model endpoint.");
        Requests++; Entered.TrySetResult();
        LastHeaders = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(',', h.Value));
        if (!LastHeaders.TryGetValue("authorization", out var token) || !token.StartsWith("Bearer fixture-grok-access-", StringComparison.Ordinal)) throw new Exception("The proxy must use the access token, not the refresh token.");
        if (Mode == "wait") await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
        if (Mode == "rate") return new(HttpStatusCode.TooManyRequests) { Content = new StringContent("do not expose token or raw quota error") };
        if (Mode == "denied") return new(HttpStatusCode.Forbidden);
        if (Mode == "unauthorized-once" && !returnedUnauthorized) { returnedUnauthorized = true; return new(HttpStatusCode.Unauthorized); }
        LastBody = await request.Content!.ReadAsStringAsync(cancellation);
        var parsed = JsonNode.Parse(LastBody)!;
        if (parsed["stream"]?.GetValue<bool>() != true || parsed["store"]?.GetValue<bool>() != false) throw new Exception("Expected streamed, non-storing native requests.");
        bool finalText = Mode == "text" || parsed["input"]!.AsArray().Any(t => t?["type"]?.ToString() == "function_call_output");
        var completed = finalText ? TextResponse() : CallResponse();
        if (Mode == "bad-snapshot") completed["output"] = new JsonArray("invalid item");
        string Frame(JsonObject frame) => "data: " + frame.ToJsonString() + "\r\n\r\n";
        string output = Frame(new JsonObject { ["type"] = "response.created", ["response"] = new JsonObject { ["id"] = "resp_fixture" } });
        if (finalText) output += Frame(new JsonObject { ["type"] = "response.output_text.delta", ["delta"] = "根据模拟 " });
        else output += Frame(new JsonObject { ["type"] = "response.function_call_arguments.delta", ["delta"] = "{\"query\":" });
        if (Mode != "truncated") output += Frame(new JsonObject { ["type"] = "response.completed", ["response"] = completed });
        return new(HttpStatusCode.OK) { Content = new StreamContent(new Fragmented(Encoding.UTF8.GetBytes(output))) { Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") } } };
    }
}
sealed class Fragmented(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
}
