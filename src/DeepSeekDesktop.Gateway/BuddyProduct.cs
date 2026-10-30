using System.Net.Http.Headers;
using System.Text.Json;
using DeepSeekDesktop.Core;

namespace DeepSeekDesktop.Gateway;

// Protocol reference: iJetLi/deepseek-harness-codearts, commit
// 6fa74723d42ff605d1de349589c21a1ac4eb7ba2 (MIT). No upstream runtime is used.
public sealed record BuddyProduct(string Provider, string Name, string Endpoint, string Platform, string ProductCode)
{
    public const string ClientVersion = "0.5.1";
    public string Host => new Uri(Endpoint).Host;
    public string ProtocolVersion => Provider == Providers.CodeBuddy ? "1.106.1" : "5.5.2";
    public string AttributionName => Provider == Providers.CodeBuddy ? "CodeBuddy" : "WorkBuddy";
    public string UserAgent(string? model = null)
    {
        if (Provider == Providers.CodeBuddy) return "CodeBuddyIDE/" + ProtocolVersion;
        bool domesticModel = model != null && new[] { "glm-", "hy", "kimi-", "minimax-" }.Any(prefix => model.StartsWith(prefix, StringComparison.Ordinal));
        string product = domesticModel ? "WorkBuddy" : "WorkBuddy AI";
        return $"WorkBuddy/{ProtocolVersion} {product}/{ProtocolVersion} CLI/{ProtocolVersion}";
    }
    public static BuddyProduct ForProvider(string provider) => provider switch
    {
        Providers.CodeBuddy => new(provider, "CodeBuddy（国内版）", "https://copilot.tencent.com", "ide", "codebuddy"),
        Providers.WorkBuddy => new(provider, "WorkBuddy（国际版）", "https://www.workbuddy.ai", "workbuddy-ai", "workbuddy"),
        _ => throw new ArgumentException("请选择 CodeBuddy 国内版或 WorkBuddy 国际版。")
    };

    public bool IsLoginHost(string host) => Providers.IsBuddyLoginHost(Provider, host);
    public bool IsCredentialDomain(string host) => Providers.IsBuddyCredentialDomain(Provider, host);

    internal HttpRequestMessage Request(HttpMethod method, string path, Account? account = null, bool control = true, string? model = null)
    {
        var request = new HttpRequestMessage(method, Endpoint + path);
        // The upstream parses these product/version identifiers (12403 if its UA
        // cannot be parsed). They describe protocol compatibility, not our app version.
        request.Headers.UserAgent.ParseAdd(UserAgent(control ? null : model));
        request.Headers.Add("X-DeepSeekDesktop-Version", ClientVersion);
        request.Headers.Add("X-Domain", Host);
        request.Headers.Add("X-Product-Code", ProductCode);
        request.Headers.Add("X-Product", control ? "SaaS" : AttributionName);
        if (!control)
        {
            request.Headers.Add("X-IDE-Name", AttributionName);
            request.Headers.Add("X-IDE-Type", AttributionName);
            request.Headers.Add("X-IDE-Version", ProtocolVersion);
        }
        if (account != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
            if (account.BuddyEnterpriseId.Length > 0)
            {
                request.Headers.Add("X-Enterprise-Id", account.BuddyEnterpriseId);
                request.Headers.Add("X-Tenant-Id", account.BuddyEnterpriseId);
            }
        }
        return request;
    }
}

public sealed record BuddyTokens(string AccessToken, string RefreshToken, long ExpiresAt, string UserId,
    string Domain, string EnterpriseId, string AccountType, long RefreshExpiresAt)
{
    public void Apply(Account account)
    {
        account.AccessToken = AccessToken;
        account.RefreshToken = RefreshToken;
        account.TokenExpiresAt = ExpiresAt;
        account.BuddyUserId = UserId;
        account.BuddyDomain = Domain;
        account.BuddyEnterpriseId = EnterpriseId;
        account.BuddyAccountType = AccountType;
        account.BuddyRefreshExpiresAt = RefreshExpiresAt;
    }
}

public sealed record BuddyModel(string Id, string Name, int? MaxInputTokens = null, int? MaxOutputTokens = null,
    bool SupportsImages = false, string[]? ReasoningEfforts = null, string? DefaultReasoningEffort = null);

public sealed record BuddyBalance(decimal Credits, int ActivePackages, DateTimeOffset CheckedAt);

internal static class BuddyHttp
{
    internal const int MaxJsonBytes = 4 * 1024 * 1024;

    internal static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        using var source = await response.Content.ReadAsStreamAsync(cancellation);
        using var target = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellation);
            if (read == 0) break;
            if (target.Length + read > MaxJsonBytes) throw new UpstreamException(502, "Buddy 上游响应过大。");
            target.Write(buffer, 0, read);
        }
        try { return JsonDocument.Parse(target.ToArray()); }
        catch (JsonException) { throw new UpstreamException(502, "Buddy 上游返回了无效的 JSON。"); }
    }

    internal static string Text(JsonElement source, string field)
    {
        if (source.ValueKind != JsonValueKind.Object || !source.TryGetProperty(field, out var value)) return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "";
    }

    internal static int Code(JsonElement source)
    {
        string value = Text(source, "code");
        return int.TryParse(value, out int code) ? code : 0;
    }

    internal static JsonElement Data(JsonElement root) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) ? data : default;

    internal static async Task<UpstreamException> ErrorAsync(BuddyProduct product, HttpResponseMessage response, CancellationToken cancellation, bool refresh = false)
    {
        int code = 0;
        try { using var body = await ReadJsonAsync(response, cancellation); code = Code(body.RootElement); }
        catch (UpstreamException) { } // An HTML error page must retain its HTTP status.
        return Failure(product, (int)response.StatusCode, code, refresh);
    }

    internal static string Field(string value, string label, bool required = false, int max = 1024)
    {
        if (value.Length > max || value.Any(c => c < 33 || c > 126) || required && value.Length == 0)
            throw new UpstreamException(502, $"Buddy 上游返回了无效的{label}。");
        return value;
    }

    internal static UpstreamException Failure(BuddyProduct product, int status, int code = 0, bool refresh = false)
    {
        if (code == 12403) return new UpstreamException(502, product.Name + "上游拒绝客户端版本标识，请更新 DeepSeek Desktop 后重试（上游代码 12403）。");
        string suffix = code == 0 ? "" : $"（上游代码 {code}）";
        int classification = status is >= 200 and < 300 && code is 401 or 403 or 429 ? code : status;
        int mapped = classification is 400 or 402 or 403 or 422 or 426 or 429 ? classification : classification == 401 ? 401 : 502;
        string detail = classification switch
        {
            401 => "授权已失效，请重新登录。",
            403 when refresh => "刷新授权被拒绝，请重新登录。",
            403 => "账号、模型权限或请求被上游拒绝，请查看官方账号状态。",
            402 or 429 => "积分不足或受到上游限流，请查看官方账号用量。",
            400 or 422 => "请求参数或当前接入方式被上游拒绝。",
            426 => "上游要求更新协议，请更新程序。",
            >= 300 and < 400 => "上游返回了重定向，已停止请求。",
            _ => $"上游请求失败（HTTP {status}）。"
        };
        if (refresh && classification is 401 or 403) mapped = 401;
        return new UpstreamException(mapped, product.Name + detail + suffix);
    }
}
