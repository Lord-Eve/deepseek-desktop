using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeepSeekDesktop.Core;
using DeepSeekDesktop.Gateway;

internal static class BuddyIdentityChecks
{
    private const string DomesticUa = "CodeBuddyIDE/1.106.1";
    private const string InternationalUa = "WorkBuddy/5.5.2 WorkBuddy AI/5.5.2 CLI/5.5.2";
    private const string InternationalDomesticModelUa = "WorkBuddy/5.5.2 WorkBuddy/5.5.2 CLI/5.5.2";

    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
        foreach (string provider in new[] { Providers.CodeBuddy, Providers.WorkBuddy })
        {
            var product = BuddyProduct.ForProvider(provider);
            string expectedUa = provider == Providers.CodeBuddy ? DomesticUa : InternationalUa;
            string expectedProduct = provider == Providers.CodeBuddy ? "CodeBuddy" : "WorkBuddy";
            string expectedVersion = provider == Providers.CodeBuddy ? "1.106.1" : "5.5.2";
            var routes = new HashSet<string>(StringComparer.Ordinal);
            using var http = new HttpClient(new MockHandler(request =>
            {
                Check(request.RequestUri!.Host == product.Host && request.RequestUri.Scheme == "https", "Identity compatibility must preserve the account's fixed regional HTTPS endpoint.");
                // Real /v3/config can return 12403 when the coding-client UA cannot
                // be parsed. The fixture enforces that contract without a live account.
                if (Header(request, "User-Agent") != expectedUa) return Rejected();
                Check(Header(request, "X-Domain") == product.Host && Header(request, "X-Product-Code") == product.ProductCode && Header(request, "X-Product") == "SaaS", "Authentication, catalogs and balance must retain regional control-plane headers.");
                string path = request.RequestUri.AbsolutePath; routes.Add(path);
                return path switch
                {
                    "/v2/plugin/auth/state" => Start(request),
                    "/v2/plugin/auth/token" => Json(new { code = 0, data = new { accessToken = "fixture-identity-login-access", refreshToken = "fixture-identity-login-refresh", expiresIn = 3600, refreshExpiresIn = 86400, domain = product.Host } }),
                    "/v2/plugin/login/account" => Account(request),
                    "/v2/plugin/auth/token/refresh" => Refresh(request),
                    "/console/enterprises/personal/models" or "/v3/config" => Json(new { code = 0, data = new { models = new[] { new { id = "deepseek-v4.1-flash", name = "DeepSeek", maxOutputTokens = 128000 } } } }),
                    "/v2/billing/meter/get-user-resource" => Json(new { code = 0, data = new { Response = new { Data = new { Accounts = new[] { new { CapacityUnit = "credit", Status = 0, CycleCapacityRemain = 100 } } } } } }),
                    _ => throw new Exception("Unexpected identity-check control route.")
                };
                HttpResponseMessage Start(HttpRequestMessage call)
                {
                    Check(call.Headers.Authorization == null && call.RequestUri!.Query == "?platform=" + product.Platform, "Browser authorization must use the selected product platform without an access token.");
                    return Json(new { code = 0, data = new { state = "fixture-identity-state", authUrl = "https://" + (provider == Providers.CodeBuddy ? "www.codebuddy.cn" : "www.workbuddy.ai") + "/login?state=fixture-identity-state" } });
                }
                HttpResponseMessage Account(HttpRequestMessage call)
                {
                    Check(call.Headers.Authorization?.Parameter == "fixture-identity-login-access", "Account lookup must retain the newly authorized access token.");
                    return Json(new { code = 0, data = new { uid = "fixture-identity-user", type = "personal" } });
                }
                HttpResponseMessage Refresh(HttpRequestMessage call)
                {
                    Check(Header(call, "X-Refresh-Token") == "fixture-identity-refresh-only" && call.Headers.Authorization?.Parameter == "fixture-identity-access-only", "Protocol metadata changes must not replace or mix account credentials.");
                    return Json(new { code = 0, data = new { accessToken = "fixture-identity-refreshed-access", refreshToken = "fixture-identity-refreshed-token", expiresIn = 3600, domain = product.Host } });
                }
            }));
            var client = new BuddyClient(http, Fixture(provider));
            var catalog = await client.FetchModelsAsync(CancellationToken.None);
            Check(catalog.Count == 1 && catalog[0].Id == "deepseek-v4.1-flash", "The actual model-fetch path must succeed against an upstream fixture that rejects the old application-only UA with 12403.");
            var balance = await client.FetchBalanceAsync(CancellationToken.None);
            Check(balance.Credits == 100m, "Balance lookup must use the same compatible product UA without sending model conversations.");
            var oauth = new BuddyOAuth(http, provider);
            var login = await oauth.LoginAsync(_ => Task.CompletedTask, CancellationToken.None);
            Check(login.UserId == "fixture-identity-user", "Browser authorization must still complete under the regional compatibility identity.");
            var refreshed = await oauth.RefreshAsync(Fixture(provider), CancellationToken.None);
            Check(refreshed.RefreshToken == "fixture-identity-refreshed-token", "Refresh must still return the rotated credential.");
            Check(routes.SetEquals(new[] { "/v2/plugin/auth/state", "/v2/plugin/auth/token", "/v2/plugin/login/account", "/v2/plugin/auth/token/refresh", "/console/enterprises/personal/models", "/v3/config", "/v2/billing/meter/get-user-resource" }), "Identity checks must exercise each real authentication, catalog and balance request constructor.");
            Check(product.AttributionName == expectedProduct && product.ProtocolVersion == expectedVersion && product.ProtocolVersion != BuddyProduct.ClientVersion, "Upstream product compatibility versions must be distinct from this application's release version.");
        }

        var modelCases = new[]
        {
            (Provider: Providers.CodeBuddy, Model: "deepseek-v4.1-flash", Ua: DomesticUa),
            (Provider: Providers.CodeBuddy, Model: "glm-5.3", Ua: DomesticUa),
            (Provider: Providers.WorkBuddy, Model: "deepseek-v4.1-flash", Ua: InternationalUa),
            (Provider: Providers.WorkBuddy, Model: "gpt-fixture", Ua: InternationalUa),
            (Provider: Providers.WorkBuddy, Model: "gemini-fixture", Ua: InternationalUa),
            (Provider: Providers.WorkBuddy, Model: "claude-fixture", Ua: InternationalUa),
            (Provider: Providers.WorkBuddy, Model: "glm-fixture", Ua: InternationalDomesticModelUa),
            (Provider: Providers.WorkBuddy, Model: "hy4-preview", Ua: InternationalDomesticModelUa),
            (Provider: Providers.WorkBuddy, Model: "kimi-fixture", Ua: InternationalDomesticModelUa),
            (Provider: Providers.WorkBuddy, Model: "minimax-fixture", Ua: InternationalDomesticModelUa),
            (Provider: Providers.WorkBuddy, Model: "new-model-fixture", Ua: InternationalUa)
        };
        foreach (var example in modelCases)
        {
            var product = BuddyProduct.ForProvider(example.Provider); int conversations = 0;
            using var http = new HttpClient(new MockHandler(request =>
            {
                conversations++;
                Check(request.RequestUri!.Host == product.Host && request.RequestUri.AbsolutePath == "/v2/chat/completions", "Chat compatibility must retain the correct regional inference endpoint.");
                Check(Header(request, "User-Agent") == example.Ua && product.UserAgent(example.Model) == example.Ua, "Chat must select the model-family UA from the selected regional product.");
                Check(Header(request, "X-Product") == product.AttributionName && Header(request, "X-IDE-Name") == product.AttributionName && Header(request, "X-IDE-Type") == product.AttributionName && Header(request, "X-IDE-Version") == product.ProtocolVersion, "Chat attribution and client-version headers must match the protocol product rather than the application name/version.");
                Check(Header(request, "X-Product-Code") == product.ProductCode && Header(request, "X-Domain") == product.Host && request.Headers.Authorization?.Parameter == "fixture-identity-access-only", "Changing protocol identity must not move credentials between regions or change their value.");
                return Stream();
            }));
            var frames = await DrainAsync(new BuddyClient(http, Fixture(example.Provider)), example.Model);
            Check(conversations == 1 && frames == 2, "Compatibility changes must preserve a complete SSE response without extra model calls.");
        }

        foreach (HttpStatusCode status in new[] { HttpStatusCode.OK, HttpStatusCode.BadRequest })
        {
            int calls = 0;
            using var http = new HttpClient(new MockHandler(_ => { calls++; return Rejected(status); }));
            bool rejected = false;
            try { await new BuddyClient(http, Fixture(Providers.WorkBuddy)).FetchModelsAsync(CancellationToken.None); }
            catch (UpstreamException ex) { rejected = SafeCompatibilityError(ex); }
            Check(rejected && calls == 2, "Model directories must report code 12403 as a client-protocol compatibility error for HTTP 200 or 400 without exposing the raw body.");
        }
        int chatAttempts = 0;
        using (var http = new HttpClient(new MockHandler(_ => { chatAttempts++; return Rejected(); })))
        {
            bool rejected = false;
            try { await DrainAsync(new BuddyClient(http, Fixture(Providers.WorkBuddy)), "deepseek-v4.1-flash"); }
            catch (UpstreamException ex) { rejected = SafeCompatibilityError(ex); }
            Check(rejected && chatAttempts == 1, "A protocol-version rejection must not replay a conversation or refresh the account.");
        }
        return checks;
    }

    private static bool SafeCompatibilityError(UpstreamException error) => error.Message.Contains("12403", StringComparison.Ordinal) && error.Message.Contains("客户端", StringComparison.Ordinal) && !error.Message.Contains("fixture-private-upstream-detail", StringComparison.Ordinal);
    private static string Header(HttpRequestMessage request, string name) => request.Headers.TryGetValues(name, out var values) ? string.Join(" ", values) : "";
    private static Account Fixture(string provider) => new()
    {
        Provider = provider, AccessToken = "fixture-identity-access-only", RefreshToken = "fixture-identity-refresh-only", BuddyUserId = "fixture-identity-user",
        BuddyDomain = BuddyProduct.ForProvider(provider).Host, TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
    };
    private static async Task<int> DrainAsync(BuddyClient client, string model)
    {
        int frames = 0;
        await foreach (var _ in client.EventsAsync(new JsonObject { ["model"] = model, ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "fixture identity" }) }, CancellationToken.None)) frames++;
        return frames;
    }
    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Rejected(HttpStatusCode status = HttpStatusCode.BadRequest) => Json(new { code = 12403, msg = "check ua, get coding copilot version error fixture-private-upstream-detail" }, status);
    private static HttpResponseMessage Stream() => new(HttpStatusCode.OK) { Content = new StringContent("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":null}]}\n\ndata: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
    private sealed class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
