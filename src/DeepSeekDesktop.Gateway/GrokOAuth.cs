using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DeepSeekDesktop.Gateway;

public sealed record GrokTokens(string AccessToken, string RefreshToken, long ExpiresAt);
public sealed record GrokAuthorization(Uri Url, string State, string Verifier);

// A code is usable only by the login attempt that created its PKCE verifier.
public sealed class GrokLoginInput : IDisposable
{
    private readonly TaskCompletionSource<string> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration cancellation;
    internal GrokLoginInput(CancellationToken token) => cancellation = token.Register(() => completion.TrySetCanceled(token));
    internal Task<string> Code => completion.Task;
    public bool CanSubmit => !completion.Task.IsCompleted;
    public bool SubmitCode(string code)
    {
        code = code.Trim();
        return code.Length is > 0 and <= 8192 && code.All(c => c is >= '!' and <= '~') && completion.TrySetResult(code);
    }
    internal void Reject() => completion.TrySetException(new UpstreamException(401, "Grok 登录未获授权，请重试。"));
    public void Dispose() { completion.TrySetCanceled(); cancellation.Dispose(); }
}

// Independent implementation of the public OAuth/PKCE protocol; reference Cherry Studio v2.1.6.
public sealed class GrokOAuth(HttpClient http, int callbackPort = 56121)
{
    public const string ClientId = "b1a00492-073a-47ea-816f-4c329264a828";
    private const string Discovery = "https://auth.x.ai/.well-known/openid-configuration";
    private string? tokenEndpoint;
    private string Redirect => $"http://127.0.0.1:{callbackPort}/callback";
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    private static string Endpoint(JsonElement json, string key)
    {
        if (!json.TryGetProperty(key, out var value) || !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "auth.x.ai" || uri.UserInfo.Length > 0 || !uri.IsDefaultPort) throw new UpstreamException(502, "Grok OAuth 返回了不受信任的认证端点。");
        return uri.AbsoluteUri;
    }
    public async Task<GrokAuthorization> AuthorizeAsync(CancellationToken cancellation)
    {
        using var response = await http.GetAsync(Discovery, cancellation);
        if (!response.IsSuccessStatusCode) throw new UpstreamException(502, "无法读取 Grok 官方登录配置，请检查网络。");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        string authorize = Endpoint(document.RootElement, "authorization_endpoint");
        tokenEndpoint = Endpoint(document.RootElement, "token_endpoint");
        string state = Base64Url(RandomNumberGenerator.GetBytes(32)), verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var parameters = new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = ClientId, ["redirect_uri"] = Redirect,
            ["scope"] = "openid profile email offline_access grok-cli:access api:access", ["state"] = state,
            ["nonce"] = Base64Url(RandomNumberGenerator.GetBytes(16)), ["code_challenge"] = Challenge(verifier), ["code_challenge_method"] = "S256"
        };
        return new(new Uri(authorize + "?" + string.Join('&', parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)))), state, verifier);
    }
    public Task<GrokTokens> ExchangeAsync(string code, string verifier, CancellationToken cancellation) => PostAsync(new() { ["grant_type"] = "authorization_code", ["client_id"] = ClientId, ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = Redirect }, "", cancellation);
    public Task<GrokTokens> RefreshAsync(string refresh, CancellationToken cancellation) => PostAsync(new() { ["grant_type"] = "refresh_token", ["client_id"] = ClientId, ["refresh_token"] = refresh }, refresh, cancellation);
    private async Task<GrokTokens> PostAsync(Dictionary<string, string> values, string oldRefresh, CancellationToken cancellation)
    {
        if (tokenEndpoint == null)
        {
            using var discovery = await http.GetAsync(Discovery, cancellation);
            if (!discovery.IsSuccessStatusCode) throw new UpstreamException(502, "无法读取 Grok 官方登录配置。");
            using var json = JsonDocument.Parse(await discovery.Content.ReadAsStringAsync(cancellation));
            tokenEndpoint = Endpoint(json.RootElement, "token_endpoint");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var content = new FormUrlEncodedContent(values);
        using var response = await http.PostAsync(tokenEndpoint, content, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            int code = (int)response.StatusCode;
            throw new UpstreamException(code is 400 or 401 or 403 ? 401 : code == 429 ? 429 : 502, code is 400 or 401 or 403 ? "Grok 授权已失效或被拒绝，请重新登录。" : code == 429 ? "Grok 登录刷新受到限流，请稍后重试。" : "Grok 登录服务暂时不可用，请稍后重试。");
        }
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var root = body.RootElement;
        string access = root.TryGetProperty("access_token", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString()! : "";
        string refresh = root.TryGetProperty("refresh_token", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : oldRefresh;
        if (access.Length < 16 || refresh.Length < 16 || access.Concat(refresh).Any(c => c < 33 || c > 126)) throw new UpstreamException(502, "Grok 登录响应缺少有效的访问或刷新令牌。");
        int seconds = root.TryGetProperty("expires_in", out var expiry) && expiry.TryGetInt32(out var ttl) ? Math.Clamp(ttl, 1, 604800) : 1800;
        return new(access, refresh, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + seconds);
    }

    public async Task<GrokTokens> LoginAsync(Func<Uri, Task> openBrowser, CancellationToken cancellation, Action<GrokLoginInput>? codeReady = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var attempt = await AuthorizeAsync(timeout.Token);
        using var input = new GrokLoginInput(timeout.Token);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => { options.Listen(IPAddress.Loopback, callbackPort); options.AddServerHeader = false; options.Limits.MaxRequestBodySize = 0; });
        await using var app = builder.Build();
        app.Run(async context =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            var supplied = Encoding.UTF8.GetBytes(context.Request.Query["state"].ToString()); var expected = Encoding.UTF8.GetBytes(attempt.State);
            if (context.Request.Method != "GET" || context.Request.Path != "/callback" || supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected)) { context.Response.StatusCode = 400; await context.Response.WriteAsync("Invalid authorization callback."); return; }
            if (context.Request.Query.ContainsKey("error")) input.Reject();
            else if (input.SubmitCode(context.Request.Query["code"].ToString())) { }
            else { context.Response.StatusCode = 400; await context.Response.WriteAsync("Missing authorization code."); return; }
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("登录回调已收到，请返回桌面程序。此页面可关闭。");
        });
        // Bind before opening the browser; a busy Cherry callback port fails without consuming a code.
        await app.StartAsync(timeout.Token);
        try { codeReady?.Invoke(input); await openBrowser(attempt.Url); string code = await input.Code.WaitAsync(timeout.Token); return await ExchangeAsync(code, attempt.Verifier, timeout.Token); }
        finally { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3)); await app.StopAsync(stop.Token); }
    }
}
