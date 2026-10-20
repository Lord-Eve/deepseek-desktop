using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeepSeekDesktop.Core;

namespace DeepSeekDesktop.Gateway;

public sealed class UpstreamException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed record ChatDelta(string Text, string Reasoning);

// Protocol reference: Lequait/dsh-glm-web a774779de6d06017ec606b82dde1e51f26e4e3fe (MIT).
// HTTP endpoints and payloads are implemented independently in C#; see THIRD-PARTY-NOTICES.md.
public sealed class QingyanClient(HttpClient http, Account account, Action<string, string>? rotated = null)
{
    private const string Origin = "https://chatglm.cn", Assistant = "65940acff94777010aa6b796";
    private string access = "";
    private DateTimeOffset expires;

    public static (string Timestamp, string Sign) Sign(long milliseconds, string nonce)
    {
        var digits = milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture).ToCharArray();
        var checksum = (digits.Sum(c => c - '0') - (digits[^2] - '0')) % 10;
        digits[^2] = (char)('0' + checksum);
        var timestamp = new string(digits);
        var sign = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes($"{timestamp}-{nonce}-8a1317a7468aa3ad86e997d08f3f31cb"))).ToLowerInvariant();
        return (timestamp, sign);
    }

    private static HttpRequestMessage Request(string path, string token, object body, bool stream = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Origin + path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Referrer = new Uri(Origin + "/main/alltoolsdetail");
        request.Headers.Add("Origin", Origin);
        request.Headers.Add("App-Name", "chatglm");
        request.Headers.Add("X-App-Fr", "browser_extension");
        request.Headers.Add("X-App-Platform", "pc");
        request.Headers.Add("X-App-Version", "0.0.1");
        request.Headers.Add("X-Lang", "zh");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36");
        string nonce = Guid.NewGuid().ToString("N");
        var sign = Sign(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), nonce);
        request.Headers.Add("X-Nonce", nonce);
        request.Headers.Add("X-Timestamp", sign.Timestamp);
        request.Headers.Add("X-Sign", sign.Sign);
        request.Headers.Add("X-Device-Id", Guid.NewGuid().ToString("N"));
        request.Headers.Add("X-Request-Id", Guid.NewGuid().ToString("N"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(stream ? "text/event-stream" : "application/json"));
        return request;
    }

    public async Task CheckAsync(CancellationToken cancellation)
    {
        if (access.Length > 0 && expires > DateTimeOffset.UtcNow.AddMinutes(5)) return;
        using var request = Request("/chatglm/user-api/user/refresh", account.RefreshToken, new { });
        using var response = await http.SendAsync(request, cancellation);
        CheckHttp(response);
        using var json = await ReadJsonAsync(response, cancellation);
        var root = json.RootElement;
        CheckCode(root);
        if (!root.TryGetProperty("result", out var result) || !result.TryGetProperty("access_token", out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new UpstreamException(502, "清言登录响应缺少访问令牌，可能是上游协议变化。");
        var next = result.TryGetProperty("refresh_token", out var refresh) && refresh.ValueKind == JsonValueKind.String ? refresh.GetString() : null;
        if (!string.IsNullOrEmpty(next) && next != account.RefreshToken)
        {
            if (next.Any(c => c < 33 || c > 126)) throw new UpstreamException(502, "清言返回了无效的登录凭据。");
            string old = account.RefreshToken;
            account.RefreshToken = next;
            try { rotated?.Invoke(old, next); }
            catch { throw new UpstreamException(503, "清言登录态已轮换，但本机保存失败。请停止服务并重新保存登录态。"); }
        }
        access = value.GetString()!;
        if (access.Any(c => c < 33 || c > 126)) { access = ""; throw new UpstreamException(502, "清言返回了无效的访问令牌。"); }
        // Community-observed lifetime, not an official guarantee. Rejected tokens are invalidated.
        expires = DateTimeOffset.UtcNow.AddHours(2);
    }

    private void CheckHttp(HttpResponseMessage response)
    {
        int status = (int)response.StatusCode;
        if (status is 401 or 403) { access = ""; throw new UpstreamException(401, "清言登录态失效或账号受限，请在浏览器确认账号状态并重新获取登录态。"); }
        if (status == 429) throw new UpstreamException(429, "清言请求受到限流，请稍后重试。");
        if (!response.IsSuccessStatusCode) throw new UpstreamException(502, $"清言上游返回 HTTP {status}，请检查网络或账号状态。");
    }

    private void CheckCode(JsonElement root)
    {
        var value = root.TryGetProperty("code", out var code) ? code : root.TryGetProperty("status", out var status) ? status : default;
        if (value.ValueKind == JsonValueKind.Undefined || !int.TryParse(value.ToString(), out int number) || number == 0) return;
        if (number is 401 or 403 or 1001 or 40102) { access = ""; throw new UpstreamException(401, "清言登录态已失效，请重新登录网页并更新登录态。"); }
        if (number == 429) throw new UpstreamException(429, "清言请求受到限流，请稍后重试。");
        throw new UpstreamException(502, $"清言拒绝请求（业务代码 {number}）。请检查网页账号状态或协议是否变化。");
    }

    public async IAsyncEnumerable<ChatDelta> ChatAsync(string prompt, bool search, [EnumeratorCancellation] CancellationToken cancellation)
    {
        await CheckAsync(cancellation);
        string conversation = "";
        try
        {
            using var request = Request("/chatglm/backend-api/assistant/stream", access, new
            {
                assistant_id = Assistant, conversation_id = "", project_id = "", chat_type = "user_chat",
                messages = new[] { new { role = "user", content = new[] { new { type = "text", text = prompt } } } },
                meta_data = new { channel = "", draft_id = "", if_plus_model = true, input_question_type = "xxxx", is_networking = search, is_test = false, platform = "pc", quote_log_id = "", cogview = new { rm_label_watermark = false } }
            }, true);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            CheckHttp(response);
            if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            {
                if (response.Content.Headers.ContentType?.MediaType == "application/json")
                {
                    using var error = await ReadJsonAsync(response, cancellation);
                    CheckCode(error.RootElement);
                }
                throw new UpstreamException(502, "清言未返回事件流，可能需要重新登录或上游协议已经变化。");
            }
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation), Encoding.UTF8);
            string text = "", reasoning = "";
            bool finished = false;
            var data = new StringBuilder();
            while (true)
            {
                string? line = await reader.ReadLineAsync(cancellation);
                if (line != null && line.Length > 0)
                {
                    if (line.StartsWith("data:", StringComparison.Ordinal)) data.Append(line[5..].TrimStart()).Append('\n');
                    if (data.Length > 4 * 1024 * 1024) throw new UpstreamException(502, "清言返回的数据帧过大。");
                    continue;
                }
                string payload = data.ToString().TrimEnd('\n'); data.Clear();
                if (payload.Length > 0 && payload != "[DONE]")
                {
                    using var frame = ParseFrame(payload);
                    var root = frame.RootElement;
                    if (root.TryGetProperty("conversation_id", out var id) && id.ValueKind == JsonValueKind.String) conversation = id.GetString() ?? conversation;
                    CheckCode(root);
                    if (root.TryGetProperty("error", out _) || root.TryGetProperty("status", out var fail) && fail.ToString() == "error") throw new UpstreamException(502, "清言中断了本次对话，请检查网页账号状态。");
                    string next = Flatten(root, "text", prompt), nextReasoning = Flatten(root, "think", prompt);
                    string delta = Delta(ref text, next), thought = Delta(ref reasoning, nextReasoning);
                    if (delta.Length > 0 || thought.Length > 0) yield return new(delta, thought);
                    finished = root.TryGetProperty("status", out var state) && state.ToString() == "finish";
                    if (finished) break;
                }
                if (line == null) break;
            }
            if (!finished) throw new UpstreamException(502, "清言响应提前中断，未收到完成标记；本次请求未计为成功。");
            if (text.Length == 0) throw new UpstreamException(502, "清言返回了空回复，请在网页端检查账号状态。");
        }
        finally
        {
            if (conversation.Length > 0)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    using var request = Request("/chatglm/backend-api/assistant/conversation/delete", access, new { assistant_id = Assistant, conversation_id = conversation });
                    using var response = await http.SendAsync(request, cleanup.Token);
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
    }

    private static JsonDocument ParseFrame(string payload)
    {
        try
        {
            var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
        }
        catch (JsonException) { }
        throw new UpstreamException(502, "清言返回了无效的数据帧，可能是上游协议变化。");
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        try
        {
            var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellation), cancellationToken: cancellation);
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
        }
        catch (JsonException) { }
        throw new UpstreamException(502, "清言返回了无效的登录响应，可能是上游协议变化。");
    }

    private static string Flatten(JsonElement frame, string type, string prompt)
    {
        if (!frame.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array) return "";
        var pieces = new List<string>();
        int index = -1;
        foreach (var part in parts.EnumerateArray())
        {
            index++;
            if (part.TryGetProperty("role", out var role) && role.ToString() == "user") continue;
            if (!part.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
            var piece = new StringBuilder();
            foreach (var item in content.EnumerateArray())
                if (item.TryGetProperty("type", out var kind) && kind.ToString() == type && item.TryGetProperty(type, out var value) && value.ValueKind == JsonValueKind.String) piece.Append(value.GetString());
            string next = piece.ToString();
            if (next.Length == 0) continue;
            // The echoed question is a separate leading part. Remove it before merging snapshots,
            // otherwise a genuine answer that starts with the question (e.g. 你好 → 你好！) is damaged.
            if (type == "text" && index == 0 && parts.GetArrayLength() > 1 && next == prompt) continue;
            int match = pieces.FindLastIndex(old => old.StartsWith(next, StringComparison.Ordinal) || next.StartsWith(old, StringComparison.Ordinal));
            if (match < 0) pieces.Add(next); else if (next.Length > pieces[match].Length) pieces[match] = next;
        }
        return string.Concat(pieces);
    }

    private static string Delta(ref string previous, string next)
    {
        if (next.Length == 0 || previous.StartsWith(next, StringComparison.Ordinal)) return "";
        if (next.StartsWith(previous, StringComparison.Ordinal)) { string delta = next[previous.Length..]; previous = next; return delta; }
        // Preserve punctuation; whitespace-only reformatting of an already emitted snapshot is not new content.
        string compact = string.Concat(next.Where(c => !char.IsWhiteSpace(c)));
        if (compact.Length > 0 && compact == string.Concat(previous.Where(c => !char.IsWhiteSpace(c)))) return "";
        previous += next;
        return next;
    }
}
