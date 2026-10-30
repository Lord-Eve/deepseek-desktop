using System.Globalization;
using System.Net;
using System.Text.Json;
using DeepSeekDesktop.Core;

namespace DeepSeekDesktop.Gateway;

public sealed class BuddyOAuth(HttpClient http, string provider)
{
    private readonly BuddyProduct product = BuddyProduct.ForProvider(provider);

    public async Task<BuddyTokens> LoginAsync(Func<Uri, Task> openBrowser, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var start = product.Request(HttpMethod.Post, "/v2/plugin/auth/state?platform=" + Uri.EscapeDataString(product.Platform));
        start.Headers.Add("X-No-Authorization", "true");
        start.Headers.Add("X-No-User-Id", "true");
        start.Headers.Add("X-No-Enterprise-Id", "true");
        start.Headers.Add("X-No-Department-Info", "true");
        using var started = await SendAsync(start, timeout.Token);
        if (!started.IsSuccessStatusCode) throw await BuddyHttp.ErrorAsync(product, started, timeout.Token);
        using var startJson = await BuddyHttp.ReadJsonAsync(started, timeout.Token);
        int startCode = BuddyHttp.Code(startJson.RootElement);
        if (!started.IsSuccessStatusCode || startCode != 0) throw BuddyHttp.Failure(product, (int)started.StatusCode, startCode);
        var startData = BuddyHttp.Data(startJson.RootElement);
        string state = BuddyHttp.Field(BuddyHttp.Text(startData, "state"), "授权状态", true, 4096);
        string address = BuddyHttp.Text(startData, "authUrl");
        if (!Uri.TryCreate(address, UriKind.Absolute, out var url) || url.Scheme != "https" || !url.IsDefaultPort || url.UserInfo.Length > 0 || !product.IsLoginHost(url.Host) || address.Length > 8192)
            throw new UpstreamException(502, "Buddy 登录服务返回了不受信任的授权网址。");
        if (provider == Providers.WorkBuddy)
        {
            var builder = new UriBuilder(url);
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(builder.Query);
            query["version"] = "5.5.2";
            query["loginSessionId"] = Guid.NewGuid().ToString();
            builder.Query = string.Join('&', query.SelectMany(pair => pair.Value.Select(value => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(value ?? ""))));
            url = builder.Uri;
        }
        await openBrowser(url);
        string queryState = "?state=" + Uri.EscapeDataString(state);
        using var tokenJson = await PollAsync("/v2/plugin/auth/token" + queryState, 11217, null, timeout.Token);
        var token = ParseTokens(BuddyHttp.Data(tokenJson.RootElement));
        var temporary = new Account { Provider = provider }; token.Apply(temporary);
        using var accountJson = await PollAsync("/v2/plugin/login/account" + queryState, 12151, temporary, timeout.Token);
        var data = BuddyHttp.Data(accountJson.RootElement);
        string user = BuddyHttp.Text(data, "uid");
        if (user.Length == 0) user = JwtText(token.AccessToken, "sub");
        string type = BuddyHttp.Text(data, "type");
        return token with
        {
            UserId = BuddyHttp.Field(user, "用户标识", true),
            EnterpriseId = BuddyHttp.Field(BuddyHttp.Text(data, "enterpriseId"), "企业标识"),
            AccountType = BuddyHttp.Field(type.Length == 0 ? "personal" : type, "账号类型")
        };
    }

    private async Task<JsonDocument> PollAsync(string path, int pendingCode, Account? account, CancellationToken cancellation)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellation);
            using var request = product.Request(HttpMethod.Get, path, account);
            if (account == null) request.Headers.Add("X-No-Authorization", "true");
            request.Headers.Add("X-No-User-Id", "true");
            request.Headers.Add("X-No-Enterprise-Id", "true");
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation); }
            catch (HttpRequestException) { continue; }
            using (response)
            {
                JsonDocument json;
                try { json = await BuddyHttp.ReadJsonAsync(response, cancellation); }
                catch (UpstreamException) when (!response.IsSuccessStatusCode) { throw BuddyHttp.Failure(product, (int)response.StatusCode); }
                int code = BuddyHttp.Code(json.RootElement);
                if (code == pendingCode || response.IsSuccessStatusCode && code == 0 && BuddyHttp.Data(json.RootElement).ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                { json.Dispose(); continue; }
                if (!response.IsSuccessStatusCode || code != 0)
                { json.Dispose(); throw BuddyHttp.Failure(product, (int)response.StatusCode, code); }
                return json;
            }
        }
    }

    public async Task<BuddyTokens> RefreshAsync(Account account, CancellationToken cancellation)
    {
        if (account.Provider != provider) throw new ArgumentException("Buddy 授权与当前平台不匹配。");
        if (account.BuddyDomain.Length > 0 && !product.IsCredentialDomain(account.BuddyDomain)) throw new UpstreamException(401, "Buddy 授权所属地区与所选平台不匹配，请重新登录。");
        if (account.RefreshToken.Length < 16 || account.BuddyRefreshExpiresAt > 0 && account.BuddyRefreshExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            throw new UpstreamException(401, product.Name + "刷新授权已失效，请重新登录。");
        using var request = product.Request(HttpMethod.Post, "/v2/plugin/auth/token/refresh", account);
        request.Headers.Add("X-Refresh-Token", account.RefreshToken);
        request.Headers.Add("X-Auth-Refresh-Source", "ide-main");
        using var response = await SendAsync(request, cancellation);
        if (!response.IsSuccessStatusCode) throw await BuddyHttp.ErrorAsync(product, response, cancellation, true);
        using var json = await BuddyHttp.ReadJsonAsync(response, cancellation);
        int code = BuddyHttp.Code(json.RootElement);
        if (!response.IsSuccessStatusCode || code != 0) throw BuddyHttp.Failure(product, (int)response.StatusCode, code, true);
        return ParseTokens(BuddyHttp.Data(json.RootElement), account);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        try { return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation); }
        catch (HttpRequestException) { throw new UpstreamException(502, product.Name + "登录服务连接失败，请检查网络。"); }
    }

    private BuddyTokens ParseTokens(JsonElement data, Account? previous = null)
    {
        string access = BuddyHttp.Field(BuddyHttp.Text(data, "accessToken"), "访问令牌", true, 32768);
        string refresh = BuddyHttp.Text(data, "refreshToken");
        if (refresh.Length == 0) refresh = previous?.RefreshToken ?? "";
        refresh = BuddyHttp.Field(refresh, "刷新令牌", true, 32768);
        if (access.Length < 16 || refresh.Length < 16) throw new UpstreamException(502, "Buddy 登录响应缺少有效令牌。");
        string tokenType = BuddyHttp.Text(data, "tokenType");
        if (tokenType.Length > 0 && !tokenType.Equals("Bearer", StringComparison.OrdinalIgnoreCase)) throw new UpstreamException(502, "Buddy 返回了不支持的令牌类型。");
        string domain = BuddyHttp.Text(data, "domain");
        domain = BuddyHttp.Field(domain.Length == 0 ? previous?.BuddyDomain is { Length: > 0 } previousDomain ? previousDomain : product.Host : domain, "认证域名");
        if (!product.IsCredentialDomain(domain)) throw new UpstreamException(502, "Buddy 授权域名与所选区域不匹配。");
        long expires = Expiry(data, "expiresAt", "expiresIn", access);
        long refreshExpiry = Expiry(data, "refreshExpiresAt", "refreshExpiresIn", refresh);
        return new(access, refresh, expires == 0 ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 300 : expires,
            previous?.BuddyUserId ?? "", domain, previous?.BuddyEnterpriseId ?? "", previous?.BuddyAccountType ?? "personal",
            refreshExpiry == 0 ? previous?.BuddyRefreshExpiresAt ?? 0 : refreshExpiry);
    }

    private static long Expiry(JsonElement data, string absoluteName, string relativeName, string token)
    {
        string absolute = BuddyHttp.Text(data, absoluteName);
        if (long.TryParse(absolute, NumberStyles.None, CultureInfo.InvariantCulture, out long numeric) && numeric > 0) return numeric > 1_000_000_000_000 ? numeric / 1000 : numeric;
        if (DateTimeOffset.TryParse(absolute, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) return date.ToUnixTimeSeconds();
        if (long.TryParse(JwtText(token, "exp"), out long jwtExpiry) && jwtExpiry > 0) return jwtExpiry;
        if (long.TryParse(BuddyHttp.Text(data, relativeName), out long ttl) && ttl is > 0 and <= 315360000)
        {
            long origin = long.TryParse(JwtText(token, "iat"), out long issued) && issued > 0 ? issued : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return origin + ttl;
        }
        return 0;
    }

    internal static string JwtText(string token, string name)
    {
        string[] parts = token.Split('.');
        if (parts.Length != 3 || parts[1].Length > 65536) return "";
        try
        {
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload));
            return BuddyHttp.Text(json.RootElement, name);
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return ""; }
    }
}
