using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeepSeekDesktop.Core;

namespace DeepSeekDesktop.Gateway;

public sealed class GrokClient(HttpClient http, Account account, Action<string, GrokTokens>? persist = null)
{
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly GrokOAuth oauth = new(http);
    private GrokTokens? unsaved;
    public GrokReplayCache Replay { get; } = new();
    public async Task EnsureAccessAsync(CancellationToken cancellation, bool force = false)
    {
        await refreshGate.WaitAsync(cancellation);
        try
        {
            if (unsaved != null) Save(unsaved);
            if (!force && account.AccessToken.Length > 0 && account.TokenExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60) return;
            var tokens = await oauth.RefreshAsync(account.RefreshToken, cancellation);
            account.AccessToken = tokens.AccessToken; account.RefreshToken = tokens.RefreshToken; account.TokenExpiresAt = tokens.ExpiresAt;
            Save(tokens);
        }
        finally { refreshGate.Release(); }
    }
    private void Save(GrokTokens tokens)
    {
        unsaved = tokens;
        try { persist?.Invoke(account.Id, tokens); unsaved = null; }
        catch { throw new UpstreamException(503, "Grok 授权已刷新，但本机保存失败。请停止服务并重新登录。"); }
    }
    private static string ClientVersion()
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok", "version.json");
            if (File.Exists(path))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                var root = json.RootElement;
                string? value = root.TryGetProperty("stable_version", out var stable) ? stable.GetString() : root.TryGetProperty("version", out var version) ? version.GetString() : null;
                if (Version.TryParse(value, out var parsed) && parsed >= new Version(1, 0, 13)) return parsed.ToString();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        return "1.0.62"; // Verified local CLI version at implementation time; metadata only, never auth.json.
    }
    private async Task<HttpResponseMessage> OpenAsync(JsonObject body, CancellationToken cancellation)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await EnsureAccessAsync(cancellation, attempt == 1);
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://cli-chat-proxy.grok.com/v1/responses")
            { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.Add("x-grok-client-identifier", "deepseek-desktop");
            request.Headers.Add("x-grok-client-version", ClientVersion());
            request.Headers.Add("x-xai-token-auth", "xai-grok-cli");
            request.Headers.Add("x-authenticateresponse", "authenticate-response");
            request.Headers.Add("x-grok-client-mode", "interactive");
            request.Headers.Add("x-grok-model-override", body["model"]!.ToString());
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { response.Dispose(); continue; }
            if (!response.IsSuccessStatusCode)
            {
                int status = (int)response.StatusCode; response.Dispose();
                throw new UpstreamException(status is 401 or 403 ? 401 : status == 429 ? 429 : 502, status is 401 or 403 ? "Grok 授权、模型权限或订阅资格被拒绝，请确认账号后重新登录。" : status == 429 ? "Grok 额度不足或受到限流，请查看官方账号用量。" : $"Grok 上游返回 HTTP {status}，请检查模型可用性或网络。");
            }
            if (response.Content.Headers.ContentType?.MediaType != "text/event-stream") { response.Dispose(); throw new UpstreamException(502, "Grok 未返回预期的 Responses 事件流。"); }
            return response;
        }
        throw new UpstreamException(401, "Grok 登录已失效，请重新登录。");
    }
    public async IAsyncEnumerable<JsonObject> EventsAsync(JsonObject request, [EnumeratorCancellation] CancellationToken cancellation)
    {
        using var response = await OpenAsync(request, cancellation);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation), Encoding.UTF8);
        var buffer = new StringBuilder(); bool complete = false;
        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellation);
            if (line != null && line.Length > 0)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal)) buffer.Append(line[5..].TrimStart()).Append('\n');
                if (buffer.Length > 4 * 1024 * 1024) throw new UpstreamException(502, "Grok 响应数据帧过大。");
                continue;
            }
            string payload = buffer.ToString().TrimEnd('\n'); buffer.Clear();
            if (payload.Length > 0 && payload != "[DONE]")
            {
                JsonObject frame;
                try { frame = JsonNode.Parse(payload) as JsonObject ?? throw new JsonException(); }
                catch (JsonException) { throw new UpstreamException(502, "Grok 返回了无效的 Responses 数据帧。"); }
                string? type = frame["type"]?.ToString();
                if (type is "error" or "response.failed" or "response.incomplete") throw new UpstreamException(502, "Grok 对话未成功完成，请检查账号额度、模型权限或请求内容。");
                complete = type == "response.completed";
                yield return frame;
                if (complete) break;
            }
            if (line == null) break;
        }
        if (!complete) throw new UpstreamException(502, "Grok 响应提前中断，未收到完成事件；没有下发工具调用。");
    }
}
