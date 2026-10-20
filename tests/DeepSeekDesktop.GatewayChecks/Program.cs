using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DeepSeekDesktop.Core;
using DeepSeekDesktop.Gateway;

int checks = 0;
void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
int Port() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
Settings SettingsFor(int quota = 30) => new() { Provider = Providers.Qingyan, Port = Port(), HourlyQuota = quota, Accounts = [new Account { Name = "本地模拟账号", Provider = Providers.Qingyan, RefreshToken = "fixture-refresh-only-0123456789" }] };
var settings = SettingsFor();
var upstream = new FakeUpstream();
string? rotated = null;
var logs = new List<string>();
await using var gateway = new QingyanGateway(settings, (old, next) => { Check(old == settings.Accounts[0].RefreshToken, "Rotation must identify the original saved account."); rotated = next; }, logs.Add, upstream);
await gateway.StartAsync();
Check(upstream.Refreshes == 1 && upstream.Chats == 0, "Startup must validate login without sending a chat.");
Check(rotated == "fixture-rotated-refresh-0123456789", "Rotated refresh tokens must be persisted by the host callback.");
using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(settings.BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(10) };
using (var response = await http.GetAsync("models")) Check(response.StatusCode == HttpStatusCode.Unauthorized, "Models must require local authentication.");
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
using (var request = new HttpRequestMessage(HttpMethod.Get, "models"))
{
    request.Headers.Add("Origin", "https://untrusted.example");
    using var response = await http.SendAsync(request);
    Check(response.StatusCode == HttpStatusCode.Forbidden && !response.Headers.Contains("Access-Control-Allow-Origin"), "Browser origins must not be allowed even with a local key.");
}
using (var response = await http.GetAsync("models"))
{
    Check(response.IsSuccessStatusCode && (await response.Content.ReadAsStringAsync()).Contains("qingyan-default"), "Model discovery must use the selected web platform alias.");
}
using (var response = await http.GetAsync("../admin/api/status")) Check(response.StatusCode == HttpStatusCode.NotFound, "Public API keys must not expose administrative routes.");
async Task<HttpResponseMessage> Chat(string json) => await http.PostAsync("chat/completions", new StringContent(json, Encoding.UTF8, "application/json"));
const string requestBody = """{"model":"qingyan-default","messages":[{"role":"user","content":"测试提问"}]}""";
using (var response = await Chat(requestBody))
{
    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    Check(response.IsSuccessStatusCode && body.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() == "你好，世界🐳！", "Non-streaming output must decode fragmented UTF-8, strip the prompt echo and deduplicate snapshots.");
}
using (var response = await Chat("""{"model":"qingyan-default","stream":true,"messages":[{"role":"system","content":"只用中文"},{"role":"user","content":"记住鲸鱼"},{"role":"assistant","content":"好的"},{"role":"user","content":[{"type":"text","text":"我说了什么"}]}]}"""))
{
    string body = await response.Content.ReadAsStringAsync();
    Check(response.Content.Headers.ContentType?.MediaType == "text/event-stream" && body.EndsWith("data: [DONE]\n\n", StringComparison.Ordinal), "A successful stream must finish with the OpenAI DONE marker.");
    var content = new StringBuilder(); bool stopped = false, thought = false;
    foreach (var line in body.Split('\n').Where(l => l.StartsWith("data: {") ))
    {
        using var frame = JsonDocument.Parse(line[6..]);
        var choice = frame.RootElement.GetProperty("choices")[0];
        var delta = choice.GetProperty("delta");
        if (delta.TryGetProperty("content", out var text)) content.Append(text.GetString());
        thought |= delta.TryGetProperty("reasoning_content", out _);
        stopped |= choice.GetProperty("finish_reason").ToString() == "stop";
    }
    Check(content.ToString() == "你好，世界🐳！" && stopped && thought, "Streaming snapshots must not duplicate text or lose reasoning deltas.");
    Check(upstream.LastPrompt.Contains("[system]\n只用中文") && upstream.LastPrompt.Contains("[assistant]\n好的") && upstream.LastPrompt.Contains("我说了什么"), "Multi-turn text must carry all roles and history into the web prompt.");
}
Check(upstream.Refreshes == 1 && upstream.Deletes == 2, "Reuse cached access tokens and delete each temporary conversation.");
using (var response = await Chat("""{"model":"qingyan-default","messages":[{"role":"user","content":"你好"}]}"""))
{
    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    Check(response.IsSuccessStatusCode && body.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() == "你好，世界🐳！", "A reply that starts with the prompt must retain its own text after the separate echoed question is removed.");
}
foreach (string invalid in new[] {
    """{"model":"qingyan-default","messages":[{"role":"user","content":"hi"}],"tools":[{}]}""",
    """{"model":"qingyan-default","messages":[{"role":"user","content":[{"type":"image_url","image_url":{"url":"https://example.test/a"}}]}]}""",
    """{"model":"deepseek-default","messages":[{"role":"user","content":"hi"}]}""",
    "{" })
{
    using var response = await Chat(invalid);
    Check(response.StatusCode == HttpStatusCode.BadRequest, "Unsupported features and invalid requests must fail explicitly before reaching upstream.");
}
Check(upstream.Chats == 3, "Rejected requests must not consume upstream calls.");
upstream.Mode = "truncated";
using (var response = await Chat(requestBody.Replace("\"messages\"", "\"stream\":true,\"messages\"")))
{
    string body = await response.Content.ReadAsStringAsync();
    Check(body.Contains("qingyan_error") && !body.Contains("[DONE]") && !body.Contains("\"finish_reason\":\"stop\""), "A truncated stream must return an error without a successful finish marker.");
}
upstream.Mode = "rate";
using (var response = await Chat(requestBody)) Check((int)response.StatusCode == 429, "Upstream rate limits must remain distinguishable.");
upstream.Mode = "bad-json";
using (var response = await Chat(requestBody)) Check((int)response.StatusCode == 502, "Malformed upstream frames must be protocol errors, not client request errors.");
upstream.Mode = "auth";
using (var response = await Chat(requestBody)) Check((int)response.StatusCode == 401, "Expired upstream login must invalidate the account.");
Check(gateway.Status("stats").GetProperty("success_requests").GetInt32() == 3 && gateway.Status("stats").GetProperty("failed_requests").GetInt32() == 4 && gateway.Status("status").GetProperty("invalid").GetInt32() == 1, "Statistics must distinguish real completions from truncation, transport errors and auth failures.");
Check(!string.Join('\n', logs).Contains("fixture-access") && !string.Join('\n', logs).Contains("fixture-refresh"), "Logs must never expose upstream credentials.");

var limitedSettings = SettingsFor(1); var limitedUpstream = new FakeUpstream();
await using (var limited = new QingyanGateway(limitedSettings, handler: limitedUpstream))
{
    await limited.StartAsync();
    using var client = new HttpClient { BaseAddress = new Uri(limitedSettings.BaseUrl + "/") };
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", limitedSettings.ApiKey);
    using var first = await client.PostAsync("chat/completions", new StringContent(requestBody, Encoding.UTF8, "application/json"));
    using var second = await client.PostAsync("chat/completions", new StringContent(requestBody, Encoding.UTF8, "application/json"));
    Check(first.IsSuccessStatusCode && (int)second.StatusCode == 429 && limitedUpstream.Chats == 1, "The configured hourly quota must prevent an extra upstream call.");
}
var invalidUpstream = new FakeUpstream { Mode = "invalid-refresh" };
await using (var invalid = new QingyanGateway(SettingsFor(), handler: invalidUpstream))
{
    await invalid.StartAsync();
    Check(invalid.Status("status").GetProperty("invalid").GetInt32() == 1 && invalid.Status("status").GetProperty("idle").GetInt32() == 0, "Business status 40102 must never be shown as a ready account.");
}
var cancelSettings = SettingsFor(); var cancelUpstream = new FakeUpstream { Mode = "wait" };
await using (var canceled = new QingyanGateway(cancelSettings, handler: cancelUpstream))
{
    await canceled.StartAsync();
    using var client = new HttpClient { BaseAddress = new Uri(cancelSettings.BaseUrl + "/") };
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cancelSettings.ApiKey);
    var pending = client.PostAsync("chat/completions", new StringContent(requestBody, Encoding.UTF8, "application/json"));
    await cancelUpstream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var watch = Stopwatch.StartNew(); await canceled.DisposeAsync();
    try { using var response = await pending; } catch (HttpRequestException) { }
    Check(watch.Elapsed < TimeSpan.FromSeconds(6), "Stopping must cancel active upstream requests and release the local listener.");
    var listener = new TcpListener(IPAddress.Loopback, cancelSettings.Port); listener.Start(); listener.Stop();
    Check(true, "The stopped local API port must be reusable.");
}
var occupiedSettings = SettingsFor(); var occupiedUpstream = new FakeUpstream();
var occupiedPort = new TcpListener(IPAddress.Loopback, occupiedSettings.Port); occupiedPort.Start();
await using (var occupied = new QingyanGateway(occupiedSettings, handler: occupiedUpstream))
{
    bool rejected = false;
    try { await occupied.StartAsync(); } catch (IOException) { rejected = true; }
    finally { occupiedPort.Stop(); }
    Check(rejected && occupiedUpstream.Refreshes == 0, "An occupied local port must fail before transmitting credentials.");
}
Console.WriteLine($"PASS: {checks} Qingyan gateway and mock upstream checks (no real accounts)");

sealed class FakeUpstream : HttpMessageHandler
{
    public int Refreshes, Chats, Deletes;
    public string Mode = "normal", LastPrompt = "";
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        if (request.RequestUri?.Host != "chatglm.cn" || request.RequestUri.Scheme != "https") throw new Exception("Credentials must only be sent to the fixed HTTPS origin.");
        if (!request.Headers.Contains("X-Sign") || !request.Headers.Contains("X-Timestamp")) throw new Exception("Signed web headers are required.");
        string path = request.RequestUri.AbsolutePath;
        if (path.EndsWith("/refresh"))
        {
            Refreshes++;
            if (request.Headers.Authorization?.Parameter != "fixture-refresh-only-0123456789") throw new Exception("Refresh must use the account refresh token.");
            return Json(Mode == "invalid-refresh" ? """{"status":40102,"message":"unauthorized"}""" : """{"code":0,"result":{"access_token":"fixture-access-only","refresh_token":"fixture-rotated-refresh-0123456789"}}""");
        }
        if (request.Headers.Authorization?.Parameter != "fixture-access-only") throw new Exception("Chat and cleanup must use an access token, not the refresh token.");
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
        if (path.EndsWith("/delete"))
        {
            if (!body.RootElement.TryGetProperty("assistant_id", out _) || !body.RootElement.TryGetProperty("conversation_id", out _)) throw new Exception("Cleanup requires both IDs.");
            Deletes++; return Json("{\"code\":0}");
        }
        Chats++; Entered.TrySetResult();
        if (Mode == "wait") await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
        if (Mode == "rate") return new(HttpStatusCode.TooManyRequests) { Content = new StringContent("upstream detail must stay private") };
        if (Mode == "auth") return new(HttpStatusCode.Unauthorized) { Content = new StringContent("fixture-access-only") };
        var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        if (content.ValueKind != JsonValueKind.Array) throw new Exception("Web content must use content blocks.");
        LastPrompt = content[0].GetProperty("text").GetString()!;
        string Frame(string text, string state) => "data: " + JsonSerializer.Serialize(new { conversation_id = "fixture-conversation", status = state, parts = new object[] { new { content = new[] { new { type = "text", text = LastPrompt } } }, new { content = new[] { new { type = "text", text } } }, new { content = new[] { new { type = "text", text } } }, new { content = new[] { new { type = "think", think = "模拟思考" } } } } }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\r\n\r\n";
        string sse = Mode == "bad-json" ? "data: {bad\n\n" : Frame("你好", "processing") + (Mode == "truncated" ? "" : Frame("你", "processing") + Frame("你好，世界🐳！", "finish"));
        // Emit literal UTF-8 and split even within multi-byte scalars.
        return new(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedStream(Encoding.UTF8.GetBytes(sse))) { Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") } } };
    }
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
}

sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => base.ReadAsync(buffer, offset, Math.Min(count, 1), cancellationToken);
}
