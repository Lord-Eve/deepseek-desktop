using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeepSeekDesktop.Core;
using DeepSeekDesktop.Gateway;

int checks = await BuddyAuthChecks.RunAsync();
void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
int Port() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
Settings Fixture(string provider = Providers.CodeBuddy, int quota = 50) => new()
{
    Provider = provider, Port = Port(), HourlyQuota = quota, Accounts = [new Account { Provider = provider, Name = "模拟 Buddy", AccessToken = "fixture-buddy-access-original", RefreshToken = "fixture-buddy-refresh-original", TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), BuddyDomain = provider == Providers.CodeBuddy ? "copilot.tencent.com" : "www.workbuddy.ai", BuddyUserId = "fixture-user", BuddyRefreshExpiresAt = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds() }]
};
JsonObject Request(bool stream = false) => JsonNode.Parse("""{"model":"deepseek-v4.1-flash","messages":[{"role":"system","content":"使用搜索结果回答，不改写规则"},{"role":"user","content":"鲸鱼是什么动物？"}],"tools":[{"type":"function","function":{"name":"mcp__exa__web_search_exa","description":"搜索资料","parameters":{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}}}],"reasoning_effort":"high"}""")!.AsObject().With("stream", stream);
HttpClient Api(Settings settings) { var api = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(settings.BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(10) }; api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey); return api; }
Task<HttpResponseMessage> Send(HttpClient api, JsonObject body, string path = "chat/completions") => api.PostAsync(path, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
JsonElement Element(JsonNode node) => JsonSerializer.SerializeToElement(node);
void Bad(Action action, string message) { bool rejected = false; try { action(); } catch (Exception ex) when (ex is ArgumentException or JsonException) { rejected = true; } Check(rejected, message); }
var catalog = new[] { new BuddyModel("deepseek-v4.1-flash", "DeepSeek V4.1 Flash", 1000000, 128000, false, ["low", "medium", "high", "xhigh", "max"], "high") };
var prepared = BuddyProtocol.Chat(Element(Request()), catalog);
Check(prepared["thinking"]?["type"]?.ToString() == "enabled" && prepared["reasoning_effort"]?.ToString() == "high" && prepared["stream"]!.GetValue<bool>(), "DeepSeek thinking requires both explicit enablement and a supported effort.");
Check(prepared["messages"]![0]!["content"]?.ToString() == "使用搜索结果回答，不改写规则", "System instructions must be preserved exactly, without identity/content rewriting.");
var testMessage = BuddyProtocol.Chat(Element(new JsonObject { ["model"] = catalog[0].Id, ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "请仅回复：连接成功" }) }), catalog);
Check(testMessage["messages"]![0]!["role"]?.ToString() == "system" && testMessage["messages"]![1]!["content"]?.ToString() == "请仅回复：连接成功", "User-only requests require a neutral leading system message without changing the user input.");
var disabled = Request().With("reasoning_effort", "none");
var noThinking = BuddyProtocol.Chat(Element(disabled), catalog);
Check(noThinking["thinking"]?["type"]?.ToString() == "disabled" && noThinking["reasoning_effort"] == null, "Explicit none must disable DeepSeek thinking without sending an invalid effort enum.");
Bad(() => BuddyProtocol.Chat(Element(Request().With("model", "invented-model")), catalog), "Unknown models must not become invented account capabilities.");
Bad(() => BuddyProtocol.Chat(Element(Request().With("reasoning_effort", "banana")), catalog), "Unsupported reasoning strengths must fail before contacting the model.");
Bad(() => BuddyProtocol.Chat(Element(Request().With("response_format", new JsonObject { ["type"] = "json_object" })), catalog), "Unsupported structured-output constraints must not be silently ignored.");
Bad(() => BuddyProtocol.Chat(Element(Request().With("logprobs", true)), catalog), "Unsupported request parameters must fail explicitly.");
Bad(() => BuddyProtocol.Chat(Element(Request().With("messages", new JsonArray(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "https://fixture.example/a.png" } }) }))), catalog), "Unsupported image input must not be silently converted to text.");
Bad(() => BuddyProtocol.Responses(Element(new JsonObject { ["model"] = catalog[0].Id, ["input"] = "你好", ["previous_response_id"] = "resp_old" }), catalog), "No-storage Responses must require full input history.");

foreach (string provider in new[] { Providers.CodeBuddy, Providers.WorkBuddy })
{
    var settings = Fixture(provider); var handler = new FakeBuddy(provider); var logs = new List<string>();
    await using var gateway = new BuddyGateway(settings, log: logs.Add, handler: handler); await gateway.StartAsync();
    Check(handler.Requests == 0 && handler.Catalogs == 2, "Startup may load the dynamic catalogs but must not send any model conversation.");
    using var api = Api(settings);
    using (var unauthenticated = new HttpClient(new HttpClientHandler { UseProxy = false }))
    using (var response = await unauthenticated.GetAsync(settings.BaseUrl + "/models")) Check(response.StatusCode == HttpStatusCode.Unauthorized, "The local API requires its own API key.");
    using (var request = new HttpRequestMessage(HttpMethod.Get, "models")) { request.Headers.Add("Origin", "https://untrusted.example"); using var response = await api.SendAsync(request); Check(response.StatusCode == HttpStatusCode.Forbidden, "Browser origins must be rejected."); }
    using (var response = await api.GetAsync("models"))
    {
        string json = await response.Content.ReadAsStringAsync();
        Check(response.IsSuccessStatusCode && json.Contains(catalog[0].Id) && !json.Contains("completion-fixture"), "The local directory must expose actual chat models and exclude completion-only models.");
    }
    using (var response = await api.GetAsync("../admin/api/status")) Check(response.StatusCode == HttpStatusCode.NotFound, "Public API must not expose desktop internal status resources.");
    using (var response = await Send(api, Request().With("model", "invented-model"))) Check((int)response.StatusCode == 400 && handler.Requests == 0, "Bad model requests must not consume upstream calls or local quota.");
    JsonObject firstMessage;
    using (var response = await Send(api, Request()))
    {
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        firstMessage = json["choices"]![0]!["message"]!.AsObject();
        Check(response.IsSuccessStatusCode && firstMessage["content"] == null && firstMessage["tool_calls"]![0]!["function"]!["name"]?.ToString() == "mcp__exa__web_search_exa" && json["choices"]![0]!["finish_reason"]?.ToString() == "tool_calls", "Completed native calls must become standard OpenAI tool_calls.");
        Check(firstMessage["reasoning_content"]?.ToString() == "先查资料" && json["usage"]?["total_tokens"]?.GetValue<int>() == 15, "Reasoning and actual upstream usage must be preserved.");
    }
    var continuation = Request(); continuation["messages"]!.AsArray().Add(firstMessage.DeepClone()); continuation["messages"]!.AsArray().Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = "call_fixture", ["content"] = "模拟 Exa：鲸鱼是哺乳动物。" });
    using (var response = await Send(api, continuation))
    {
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Check(response.IsSuccessStatusCode && json["choices"]![0]!["message"]!["content"]?.ToString() == "鲸鱼是哺乳动物。", "Client-side tools must complete a native multi-turn round trip.");
        Check(handler.LastRequest!["messages"]![2]!["reasoning_content"]?.ToString() == "先查资料" && handler.LastRequest["messages"]![3]!["tool_call_id"]?.ToString() == "call_fixture", "Native reasoning and tool result IDs must survive the continuation without a replay cache.");
    }
    using (var response = await Send(api, Request(true)))
    {
        string body = await response.Content.ReadAsStringAsync();
        Check(response.IsSuccessStatusCode && body.Contains("\"index\":0") && body.Contains("\"finish_reason\":\"tool_calls\"") && body.EndsWith("data: [DONE]\n\n", StringComparison.Ordinal), "Streaming tool calls must be indexed and emitted only after full validation.");
    }
    handler.Mode = "text";
    using (var response = await Send(api, Request(true).With("stream_options", new JsonObject { ["include_usage"] = true })))
    {
        string body = await response.Content.ReadAsStringAsync();
        Check(response.IsSuccessStatusCode && body.Contains("\"content\":") && body.Contains("\"finish_reason\":\"stop\"") && body.Contains("\"total_tokens\":15") && body.EndsWith("data: [DONE]\n\n", StringComparison.Ordinal), "Live text SSE must finish normally and include requested actual usage.");
    }
    var native = new JsonObject { ["model"] = catalog[0].Id, ["input"] = "你好", ["store"] = false, ["reasoning"] = new JsonObject { ["effort"] = "low" } };
    using (var response = await Send(api, native, "responses"))
    {
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Check(response.IsSuccessStatusCode && json["object"]?.ToString() == "response" && json["status"]?.ToString() == "completed" && json["output"]![0]!["content"]![0]!["text"]?.ToString() == "鲸鱼是哺乳动物。" && json["usage"]?["input_tokens"]?.GetValue<int>() == 10, "Responses text and usage must convert from the native Chat reply.");
    }
    using (var response = await Send(api, native.With("stream", true), "responses"))
    {
        string body = await response.Content.ReadAsStringAsync();
        Check(response.IsSuccessStatusCode && body.Contains("event: response.output_text.delta") && body.Contains("event: response.completed") && !body.Contains("chat.completion.chunk"), "Responses SSE must emit correctly typed native-style events.");
    }
    handler.Mode = "calls";
    var responsesCall = new JsonObject { ["model"] = catalog[0].Id, ["input"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "查鲸鱼" }), ["tools"] = new JsonArray(new JsonObject { ["type"] = "function", ["name"] = "mcp__exa__web_search_exa", ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string" } } } }), ["tool_choice"] = "required" };
    using (var response = await Send(api, responsesCall, "responses"))
    {
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Check(response.IsSuccessStatusCode && json["output"]!.AsArray().Any(v => v?["type"]?.ToString() == "function_call"), "Responses must expose real function_call output items.");
        var followup = (JsonObject)responsesCall.DeepClone(); followup["tool_choice"] = "auto";
        foreach (var item in json["output"]!.AsArray()) followup["input"]!.AsArray().Add(item!.DeepClone());
        followup["input"]!.AsArray().Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = "call_fixture", ["output"] = "模拟 Exa：鲸鱼是哺乳动物。" });
        using var final = await Send(api, followup, "responses");
        Check(final.IsSuccessStatusCode && handler.LastRequest!["messages"]!.AsArray().Any(m => m?["role"]?.ToString() == "tool") && handler.LastRequest["messages"]!.AsArray().Any(m => m?["reasoning_content"]?.ToString() == "先查资料"), "Responses function results and reasoning must round-trip into native Chat history.");
    }
    handler.Mode = "truncated";
    using (var response = await Send(api, Request(true)))
    {
        string body = await response.Content.ReadAsStringAsync();
        Check(!body.Contains("\"tool_calls\"") && !body.Contains("[DONE]") && !body.Contains("\"finish_reason\":\"tool_calls\""), "Truncated tool streams must never become executable calls or normal completion.");
    }
    handler.Mode = "badargs";
    using (var response = await Send(api, Request())) Check((int)response.StatusCode == 502 && !(await response.Content.ReadAsStringAsync()).Contains("\"tool_calls\""), "Incomplete tool arguments must fail even if upstream declares success.");
    handler.Mode = "undeclared";
    using (var response = await Send(api, Request())) Check((int)response.StatusCode == 502, "Upstream must not invoke undeclared tools.");
    handler.Mode = "rate";
    using (var response = await Send(api, Request())) Check((int)response.StatusCode == 429, "Upstream rate limits must be distinguishable from authorization errors.");
    handler.Mode = "unauthorized-once"; int beforeRequests = handler.Requests;
    using (var response = await Send(api, Request())) Check(response.IsSuccessStatusCode && handler.Requests == beforeRequests + 2 && handler.Refreshes == 1, "A pre-stream HTTP 401 may refresh and retry only once.");
    Check(!string.Join('\n', logs).Contains("fixture-buddy-access", StringComparison.Ordinal) && gateway.Status("stats").GetProperty("failed_requests").GetInt32() == 4, "Stats must count failed calls while logs exclude credentials and raw private errors.");
}

var quotaSettings = Fixture(quota: 1); var quotaHandler = new FakeBuddy(Providers.CodeBuddy) { Mode = "text" };
await using (var gateway = new BuddyGateway(quotaSettings, handler: quotaHandler))
{
    await gateway.StartAsync(); using var api = Api(quotaSettings);
    using var first = await Send(api, Request()); using var second = await Send(api, Request());
    Check(first.IsSuccessStatusCode && (int)second.StatusCode == 429 && quotaHandler.Requests == 1, "The local hourly limit must prevent the extra upstream request.");
}
var canceledSettings = Fixture(); var waitingHandler = new FakeBuddy(Providers.CodeBuddy) { Mode = "wait" };
await using (var gateway = new BuddyGateway(canceledSettings, handler: waitingHandler))
{
    await gateway.StartAsync(); using var api = Api(canceledSettings); var pending = Send(api, Request());
    await waitingHandler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var watch = Stopwatch.StartNew(); await gateway.DisposeAsync();
    try { using var response = await pending; } catch (HttpRequestException) { }
    var reuse = new TcpListener(IPAddress.Loopback, canceledSettings.Port); reuse.Start(); reuse.Stop();
    Check(watch.Elapsed < TimeSpan.FromSeconds(6), "Stopping must cancel upstream work and release the local port.");
}
Console.WriteLine($"PASS: {checks} Buddy auth, protocol, local API and mock tool round-trip checks (no real login or model calls)");

static class JsonTestExtensions
{
    public static JsonObject With(this JsonObject value, string key, JsonNode? content) { value[key] = content; return value; }
}
sealed class FakeBuddy(string provider) : HttpMessageHandler
{
    public string Mode = "calls";
    public int Requests, Refreshes, Catalogs;
    public JsonObject? LastRequest;
    private bool retried;
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        string expected = provider == Providers.CodeBuddy ? "copilot.tencent.com" : "www.workbuddy.ai";
        if (request.RequestUri?.Scheme != "https" || request.RequestUri.Host != expected || request.Headers.GetValues("X-Domain").Single() != expected) throw new Exception("A credential was sent outside its own fixed region.");
        string path = request.RequestUri.AbsolutePath;
        if (path is "/console/enterprises/personal/models" or "/v3/config")
        {
            Catalogs++;
            return Json(new { code = 0, data = new { models = new object[] { new { id = "deepseek-v4.1-flash", name = "DeepSeek V4.1 Flash", maxInputTokens = 1000000, maxOutputTokens = 128000, reasoning = new { supportedEfforts = new[] { "low", "medium", "high", "xhigh", "max" }, defaultEffort = "high" } }, new { id = "completion-fixture", maxOutputTokens = 128 } }, agents = new[] { new { name = "craft", models = new[] { "deepseek-v4.1-flash" } } } } });
        }
        if (path == "/v2/plugin/auth/token/refresh") { Refreshes++; return Json(new { code = 0, data = new { accessToken = "fixture-buddy-access-rotated", refreshToken = "fixture-buddy-refresh-rotated", expiresIn = 3600, refreshExpiresIn = 86400, domain = expected } }); }
        if (path != "/v2/chat/completions") throw new Exception("Unexpected upstream request in the gateway contract.");
        Requests++; Entered.TrySetResult();
        if (request.Headers.Authorization?.Parameter?.StartsWith("fixture-buddy-access-", StringComparison.Ordinal) != true) throw new Exception("Chat must use the access token rather than the refresh token.");
        if (Mode == "wait") await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
        if (Mode == "rate") return Json(new { code = 11135, message = "private upstream error fixture-buddy-access must not be reflected" }, HttpStatusCode.TooManyRequests);
        if (Mode == "unauthorized-once" && !retried) { retried = true; return Json(new { code = 401 }, HttpStatusCode.Unauthorized); }
        LastRequest = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellation))!.AsObject();
        if (LastRequest["stream"]?.GetValue<bool>() != true || LastRequest["messages"] is not JsonArray messages) throw new Exception("Expected native streamed Chat request.");
        if (messages[0]?["role"]?.ToString() != "system") throw new Exception("The upstream requires a leading system message.");
        bool final = Mode is "text" or "unauthorized-once" || messages.Any(m => m?["role"]?.ToString() == "tool");
        string Frame(JsonObject delta, string? finish = null) => "data: " + new JsonObject { ["id"] = "chatcmpl-fixture", ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish }) }.ToJsonString() + "\r\n\r\n";
        var output = new StringBuilder(Frame(new JsonObject { ["role"] = "assistant" }));
        if (final) output.Append(Frame(new JsonObject { ["content"] = "鲸鱼是" })).Append(Frame(new JsonObject { ["content"] = "哺乳动物。" }));
        else
        {
            output.Append(Frame(new JsonObject { ["reasoning_content"] = "先查资料" }));
            output.Append(Frame(new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = 0, ["id"] = "call_fixture", ["type"] = "function", ["function"] = new JsonObject { ["name"] = Mode == "undeclared" ? "invented_tool" : "mcp__exa__web_search_exa", ["arguments"] = "{\"query\":" } }) }));
            if (Mode != "badargs") output.Append(Frame(new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = 0, ["function"] = new JsonObject { ["arguments"] = "\"鲸鱼\"}" } }) }));
        }
        if (Mode != "truncated")
        {
            output.Append(Frame(new JsonObject(), final ? "stop" : "tool_calls"));
            output.Append("data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5,\"total_tokens\":15}}\r\n\r\n");
            output.Append("data: [DONE]\r\n\r\n");
        }
        return new(HttpStatusCode.OK) { Content = new StreamContent(new Fragmented(Encoding.UTF8.GetBytes(output.ToString()))) { Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") } } };
    }
}
sealed class Fragmented(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
}
