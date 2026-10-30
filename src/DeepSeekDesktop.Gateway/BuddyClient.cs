using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeepSeekDesktop.Core;

namespace DeepSeekDesktop.Gateway;

public sealed class BuddyClient(HttpClient http, Account account, Action<string, BuddyTokens>? persist = null)
{
    private readonly BuddyProduct product = BuddyProduct.ForProvider(account.Provider);
    private readonly BuddyOAuth oauth = new(http, account.Provider);
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private BuddyTokens? unsaved;

    public async Task EnsureAccessAsync(CancellationToken cancellation, bool force = false)
    {
        await refreshGate.WaitAsync(cancellation);
        try
        {
            if (unsaved != null) Save(unsaved);
            if (!force && account.AccessToken.Length >= 16 && account.TokenExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60) return;
            var next = await oauth.RefreshAsync(account, cancellation);
            next.Apply(account);
            Save(next);
        }
        finally { refreshGate.Release(); }
    }

    private void Save(BuddyTokens tokens)
    {
        unsaved = tokens;
        try { persist?.Invoke(account.Id, tokens); unsaved = null; }
        catch { throw new UpstreamException(503, product.Name + "授权已刷新，但本机保存失败。请停止服务后重新保存授权。"); }
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> create, CancellationToken cancellation)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await EnsureAccessAsync(cancellation, attempt == 1);
            using var request = create();
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation); }
            catch (HttpRequestException) { throw new UpstreamException(502, product.Name + "上游连接失败，请检查网络。"); }
            // A rejected authorization header precedes any response stream. Never replay
            // a chat after a transport failure, 403, 429, or after receiving SSE data.
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { response.Dispose(); continue; }
            return response;
        }
        throw new UpstreamException(401, product.Name + "授权已失效，请重新登录。");
    }

    public async Task<IReadOnlyList<BuddyModel>> FetchModelsAsync(CancellationToken cancellation)
    {
        var models = new Dictionary<string, BuddyModel>(StringComparer.Ordinal);
        UpstreamException? failure = null;
        // The catalogs are complementary. The scoped catalog has more complete
        // metadata; /v3/config also contains active model variants absent there.
        foreach (string path in new[] { "/console/enterprises/personal/models", "/v3/config" })
        {
            try
            {
                using var response = await SendAsync(() => product.Request(HttpMethod.Get, path, account), cancellation);
                if (!response.IsSuccessStatusCode) throw await BuddyHttp.ErrorAsync(product, response, cancellation);
                using var json = await BuddyHttp.ReadJsonAsync(response, cancellation);
                int code = BuddyHttp.Code(json.RootElement);
                if (code != 0) throw BuddyHttp.Failure(product, (int)response.StatusCode, code);
                foreach (var model in ParseModels(json.RootElement)) models.TryAdd(model.Id, model);
            }
            catch (UpstreamException ex) when (ex.Status is not (401 or 503)) { failure = ex; }
            catch (HttpRequestException) { failure = new UpstreamException(502, product.Name + "模型目录连接失败，请检查网络。"); }
        }
        if (models.Count == 0) throw failure ?? new UpstreamException(502, product.Name + "没有返回可识别的对话模型，请确认账号权限后重试。");
        return models.Values.ToArray();
    }

    private static IReadOnlyList<BuddyModel> ParseModels(JsonElement body)
    {
        var data = BuddyHttp.Data(body);
        if (data.ValueKind != JsonValueKind.Object) return [];
        var metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (data.TryGetProperty("models", out var declared) && declared.ValueKind == JsonValueKind.Array)
            foreach (var entry in declared.EnumerateArray())
            {
                string id = BuddyHttp.Text(entry, "id");
                if (ValidModelId(id)) metadata.TryAdd(id, entry);
            }
        var ordered = new List<string>();
        if (data.TryGetProperty("agents", out var agents) && agents.ValueKind == JsonValueKind.Array)
            foreach (string preferred in new[] { "cli", "craft" })
            {
                var agent = agents.EnumerateArray().FirstOrDefault(a => BuddyHttp.Text(a, "name") == preferred);
                if (agent.ValueKind != JsonValueKind.Object || !agent.TryGetProperty("models", out var referenced) || referenced.ValueKind != JsonValueKind.Array) continue;
                ordered.AddRange(referenced.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!));
                break;
            }
        ordered.AddRange(metadata.Keys);
        if (data.TryGetProperty("productFeaturesConfig", out var features) && features.ValueKind == JsonValueKind.Object
            && features.TryGetProperty("ModelTrialBanner", out var trial) && trial.ValueKind == JsonValueKind.Object
            && trial.TryGetProperty("banners", out var banners) && banners.ValueKind == JsonValueKind.Array)
            ordered.AddRange(banners.EnumerateArray().Select(v => BuddyHttp.Text(v, "targetModelId")));
        var result = new List<BuddyModel>();
        foreach (string id in ordered.Distinct(StringComparer.Ordinal))
        {
            if (!ValidModelId(id) || id is "auto" or "default" || id.StartsWith("nes-", StringComparison.Ordinal) || id.StartsWith("completion-", StringComparison.Ordinal) || id.StartsWith("codewise-", StringComparison.Ordinal)) continue;
            metadata.TryGetValue(id, out var entry);
            if (entry.ValueKind == JsonValueKind.Object)
            {
                if (entry.TryGetProperty("supportsExtra", out var extra) && extra.ValueKind == JsonValueKind.True) continue;
                if (PositiveInt(entry, "maxOutputTokens") is > 0 and <= 256) continue;
                if (entry.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array && tags.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String && v.GetString() == "text-to-image")) continue;
            }
            string name = BuddyHttp.Text(entry, "name");
            string[]? efforts = null; string? defaultEffort = null;
            if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.Object)
            {
                if (reasoning.TryGetProperty("supportedEfforts", out var values) && values.ValueKind == JsonValueKind.Array)
                    efforts = values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String && v.GetString() is "none" or "low" or "medium" or "high" or "xhigh" or "max").Select(v => v.GetString()!).Distinct().ToArray();
                string value = BuddyHttp.Text(reasoning, "defaultEffort");
                if (value is "none" or "low" or "medium" or "high" or "xhigh" or "max") defaultEffort = value;
            }
            bool images = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("supportsImages", out var imageValue) && imageValue.ValueKind == JsonValueKind.True;
            result.Add(new(id, name.Length is > 0 and <= 160 && !name.Any(char.IsControl) ? name : id, PositiveInt(entry, "maxInputTokens"), PositiveInt(entry, "maxOutputTokens"), images, efforts, defaultEffort));
        }
        return result;
    }

    private static int? PositiveInt(JsonElement entry, string name) => entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && number > 0 ? number : null;
    private static bool ValidModelId(string id) => id.Length is > 0 and <= 160 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':' or '/');

    public async Task<BuddyBalance> FetchBalanceAsync(CancellationToken cancellation)
    {
        using var response = await SendAsync(() =>
        {
            var request = product.Request(HttpMethod.Post, "/v2/billing/meter/get-user-resource", account);
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            if (account.BuddyUserId.Length > 0) request.Headers.Add("X-User-Id", account.BuddyUserId);
            return request;
        }, cancellation);
        if (!response.IsSuccessStatusCode) throw await BuddyHttp.ErrorAsync(product, response, cancellation);
        using var json = await BuddyHttp.ReadJsonAsync(response, cancellation);
        int code = BuddyHttp.Code(json.RootElement);
        if (code != 0) throw BuddyHttp.Failure(product, (int)response.StatusCode, code);
        var data = BuddyHttp.Data(json.RootElement);
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("Response", out var outer) || outer.ValueKind != JsonValueKind.Object
            || !outer.TryGetProperty("Data", out var inner) || inner.ValueKind != JsonValueKind.Object
            || !inner.TryGetProperty("Accounts", out var packages) || packages.ValueKind != JsonValueKind.Array)
            throw new UpstreamException(502, product.Name + "积分响应格式无效，余额未知。");
        decimal credits = 0; int active = 0; var now = DateTimeOffset.UtcNow;
        foreach (var package in packages.EnumerateArray())
        {
            if (package.ValueKind != JsonValueKind.Object) throw new UpstreamException(502, "Buddy 积分资源包格式无效，余额未知。");
            string unit = BuddyHttp.Text(package, "CapacityUnit");
            if (unit.Length == 0) unit = BuddyHttp.Text(package, "OriginUnit");
            if (!unit.Equals("credit", StringComparison.OrdinalIgnoreCase) && !unit.Equals("credits", StringComparison.OrdinalIgnoreCase)) continue;
            if (BuddyHttp.Text(package, "Status") == "3") continue;
            if (long.TryParse(BuddyHttp.Text(package, "DeductionEndTime"), out long end) && end > 0 && (end > 1_000_000_000_000 ? end / 1000 : end) <= now.ToUnixTimeSeconds()) continue;
            if (package.TryGetProperty("ExpiredTime", out var expiryValue) && expiryValue.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null))
                throw new UpstreamException(502, product.Name + "积分资源包的到期时间无法识别，余额未知。");
            string expiry = BuddyHttp.Text(package, "ExpiredTime");
            if (expiry.Length > 0)
            {
                if (!TryPackageDate(expiry, out var expires)) throw new UpstreamException(502, product.Name + "积分资源包的到期时间无法识别，余额未知。");
                if (expires <= now) continue;
            }
            string remaining = BuddyHttp.Text(package, "CycleCapacityRemainPrecise");
            if (remaining.Length == 0) remaining = BuddyHttp.Text(package, "CycleCapacityRemain");
            if (!decimal.TryParse(remaining, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal amount) || amount < 0)
                throw new UpstreamException(502, product.Name + "积分资源包缺少本周期余额，余额未知。");
            try { credits = checked(credits + amount); } catch (OverflowException) { throw new UpstreamException(502, "Buddy 积分数值无效。"); }
            active++;
        }
        return new(decimal.Round(credits, 2, MidpointRounding.AwayFromZero), active, now);
    }

    private static bool TryPackageDate(string text, out DateTimeOffset value)
    {
        value = default;
        string normalized = text.Trim();
        if (long.TryParse(normalized, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long unix))
        {
            try
            {
                value = unix is > 100_000_000_000 or < -100_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(unix) : DateTimeOffset.FromUnixTimeSeconds(unix);
                return true;
            }
            catch (ArgumentOutOfRangeException) { return false; }
        }
        bool offset = normalized.EndsWith('Z') || normalized.EndsWith('z')
            || normalized.Length > 10 && (normalized.IndexOf('+', 10) >= 0 || normalized.IndexOf('-', 10) >= 0);
        if (offset) return DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
        // Date-only and unzoned package timestamps are Beijing wall-clock time;
        // constructing the offset explicitly avoids the computer's local timezone.
        normalized = normalized.Replace(' ', 'T');
        string[] formats = ["yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"];
        if (!DateTime.TryParseExact(normalized, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var wallTime)) return false;
        try { value = new DateTimeOffset(DateTime.SpecifyKind(wallTime, DateTimeKind.Unspecified), TimeSpan.FromHours(8)); return true; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    public async IAsyncEnumerable<JsonObject> EventsAsync(JsonObject request, [EnumeratorCancellation] CancellationToken cancellation)
    {
        JsonObject body = (JsonObject)request.DeepClone(); body["stream"] = true;
        string conversation = Guid.NewGuid().ToString("N");
        using var response = await SendAsync(() =>
        {
            var message = product.Request(HttpMethod.Post, "/v2/chat/completions", account, false, body["model"]?.GetValue<string>());
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            message.Headers.Add("X-Agent-Purpose", "conversation");
            message.Headers.Add("X-Conversation-ID", conversation);
            message.Headers.Add("X-Conversation-Request-ID", conversation);
            message.Headers.Add("X-Conversation-Message-ID", Guid.NewGuid().ToString("N"));
            message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            return message;
        }, cancellation);
        if (!response.IsSuccessStatusCode)
        {
            throw await BuddyHttp.ErrorAsync(product, response, cancellation);
        }
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            throw new UpstreamException(502, product.Name + "未返回预期的对话事件流。");
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation), Encoding.UTF8);
        var buffer = new StringBuilder(); bool finished = false, done = false; int total = 0;
        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellation);
            if (line != null && line.Length > 0)
            {
                total += line.Length;
                if (line.Length > BuddyHttp.MaxJsonBytes || total > 16 * 1024 * 1024) throw new UpstreamException(502, "Buddy 对话事件流过大。");
                if (line.StartsWith("data:", StringComparison.Ordinal)) buffer.Append(line[5..].TrimStart()).Append('\n');
                if (buffer.Length > BuddyHttp.MaxJsonBytes) throw new UpstreamException(502, "Buddy 对话数据帧过大。");
                continue;
            }
            string payload = buffer.ToString().TrimEnd('\n'); buffer.Clear();
            if (payload == "[DONE]") { done = true; break; }
            if (payload.Length > 0)
            {
                JsonObject frame;
                try { frame = JsonNode.Parse(payload) as JsonObject ?? throw new JsonException(); }
                catch (JsonException) { throw new UpstreamException(502, "Buddy 返回了无效的对话数据帧。"); }
                if (frame.ContainsKey("error") || frame["code"] is JsonValue business && business.TryGetValue<int>(out int code) && code != 0)
                    throw new UpstreamException(502, product.Name + "对话被上游中止，请检查模型权限、内容或账号额度。");
                if (frame["choices"] is JsonArray choices)
                    foreach (var choice in choices.OfType<JsonObject>())
                        if (choice["finish_reason"] is JsonValue value && value.TryGetValue<string>(out var reason) && !string.IsNullOrEmpty(reason)) finished = true;
                yield return frame;
            }
            if (line == null) break;
        }
        if (!finished || !done) throw new UpstreamException(502, product.Name + "回复提前中断，未收到完整结束标记。");
    }
}
