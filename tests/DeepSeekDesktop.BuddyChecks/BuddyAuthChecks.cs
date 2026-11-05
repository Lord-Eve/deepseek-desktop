using System.Net;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DeepSeekDesktop.Core;
using DeepSeekDesktop.Gateway;

internal static class BuddyAuthChecks
{
    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
        foreach (string provider in new[] { Providers.CodeBuddy, Providers.WorkBuddy })
        {
            var product = BuddyProduct.ForProvider(provider);
            string loginHost = provider == Providers.CodeBuddy ? "www.codebuddy.cn" : "www.workbuddy.ai";
            Uri? opened = null; int tokenPolls = 0;
            using var http = new HttpClient(new MockHandler(request =>
            {
                Check(request.RequestUri!.Host == product.Host, "Authorization requests must remain in the selected region.");
                Check(request.Headers.GetValues("X-Domain").Single() == product.Host, "The domain header must match the selected region.");
                return request.RequestUri.AbsolutePath switch
                {
                    "/v2/plugin/auth/state" => Json(new { code = 0, data = new { state = "fixture-state", authUrl = $"https://{loginHost}/login/?state=fixture-state" } }),
                    "/v2/plugin/auth/token" when tokenPolls++ == 0 => Json(new { code = 11217, data = (object?)null }),
                    "/v2/plugin/auth/token" => Json(new { code = 0, data = new { accessToken = "fixture-access-only", refreshToken = "fixture-refresh-only", expiresIn = 1800, refreshExpiresIn = 86400, domain = product.Host, tokenType = "Bearer" } }),
                    "/v2/plugin/login/account" => AccountResponse(request),
                    _ => throw new Exception("Unexpected authorization route.")
                };
                HttpResponseMessage AccountResponse(HttpRequestMessage accountRequest)
                {
                    Check(accountRequest.Headers.Authorization?.Parameter == "fixture-access-only", "Account polling must use the newly authorized access token.");
                    return Json(new { code = 0, data = new { uid = "fixture-user", type = "personal", enterpriseId = "" } });
                }
            }));
            long before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var tokens = await new BuddyOAuth(http, provider).LoginAsync(url => { opened = url; return Task.CompletedTask; }, CancellationToken.None);
            Check(opened?.Host == loginHost && opened.Scheme == "https", "Login must open the selected product's official authorization page.");
            Check(provider != Providers.WorkBuddy || opened!.Query.Contains("loginSessionId="), "International authorization must include a fresh login session.");
            Check(tokens.UserId == "fixture-user" && tokens.Domain == product.Host && tokens.ExpiresAt >= before + 1800 && tokens.RefreshExpiresAt >= before + 86400, "Login must combine identity and relative token lifetimes.");
        }

        foreach (string unsafeUrl in new[] { "https://www.workbuddy.ai.attacker.test/login", "http://www.workbuddy.ai/login", "https://www.workbuddy.ai:8443/login", "https://user:password@www.workbuddy.ai/login", "https://www.codebuddy.cn/login" })
        {
            bool opened = false; int requests = 0;
            using var http = new HttpClient(new MockHandler(_ => { requests++; return Json(new { code = 0, data = new { state = "fixture-state", authUrl = unsafeUrl } }); }));
            await RejectAsync(() => new BuddyOAuth(http, Providers.WorkBuddy).LoginAsync(_ => { opened = true; return Task.CompletedTask; }, CancellationToken.None), 502);
            Check(!opened && requests == 1, "Untrusted or cross-region authorization URLs must be rejected before opening a browser or exchanging tokens.");
        }

        using (var cancel = new CancellationTokenSource())
        using (var http = new HttpClient(new MockHandler(_ => Json(new { code = 0, data = new { state = "fixture-state", authUrl = "https://www.workbuddy.ai/login" } }))))
        {
            bool cancelled = false;
            try { await new BuddyOAuth(http, Providers.WorkBuddy).LoginAsync(_ => { cancel.Cancel(); return Task.CompletedTask; }, cancel.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Cancelling browser authorization must stop token polling.");
        }

        int slowPolls = 0;
        using (var http = new HttpClient(new MockHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v2/plugin/auth/state" => Json(new { code = 0, data = new { state = "fixture-state", authUrl = "https://www.workbuddy.ai/login" } }),
            "/v2/plugin/auth/token" when ++slowPolls == 1 => throw new TaskCanceledException("fixture-single-request-timeout"),
            "/v2/plugin/auth/token" => Json(new { code = 0, data = new { accessToken = "fixture-access-only", refreshToken = "fixture-refresh-only", expiresIn = 1800 } }),
            "/v2/plugin/login/account" => Json(new { code = 0, data = new { uid = "fixture-user", type = "personal" } }),
            _ => throw new Exception("Unexpected timeout recovery route.")
        })))
        {
            var recovered = await new BuddyOAuth(http, Providers.WorkBuddy).LoginAsync(_ => Task.CompletedTask, CancellationToken.None);
            Check(slowPolls == 2 && recovered.UserId == "fixture-user", "A single HTTP polling timeout must not cancel the five-minute browser authorization attempt.");
        }

        using (var cancel = new CancellationTokenSource())
        {
            int cancelledPolls = 0;
            using var http = new HttpClient(new MockHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/v2/plugin/auth/state") return Json(new { code = 0, data = new { state = "fixture-state", authUrl = "https://www.workbuddy.ai/login" } });
                cancelledPolls++; cancel.Cancel();
                throw new TaskCanceledException("fixture-external-cancel", null, cancel.Token);
            }));
            bool cancelled = false;
            try { await new BuddyOAuth(http, Providers.WorkBuddy).LoginAsync(_ => Task.CompletedTask, cancel.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && cancelledPolls == 1, "Cancelling an in-flight poll must terminate authorization without another token or account request.");
        }

        var account = Fixture();
        long expiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        using (var http = new HttpClient(new MockHandler(request =>
        {
            Check(request.RequestUri!.Host == "www.workbuddy.ai" && request.Headers.GetValues("X-Refresh-Token").Single() == account.RefreshToken, "Refresh must use the selected host and current refresh token.");
            return Json(new { code = 0, data = new { accessToken = "fixture-access-rotated", refreshToken = "fixture-refresh-rotated", expiresAt = expiry * 1000, refreshExpiresIn = 86400 } });
        })))
        {
            var rotated = await new BuddyOAuth(http, Providers.WorkBuddy).RefreshAsync(account, CancellationToken.None);
            Check(rotated.ExpiresAt == expiry && rotated.UserId == account.BuddyUserId && rotated.Domain == account.BuddyDomain && rotated.RefreshToken == "fixture-refresh-rotated", "Refresh must normalize milliseconds and preserve identity when omitted by the server.");
            rotated.Apply(account);
            Check(account.AccessToken == "fixture-access-rotated" && account.RefreshToken == "fixture-refresh-rotated", "Token rotation must update both access and refresh credentials.");
        }
        using (var http = new HttpClient(new MockHandler(_ => Json(new { code = 403, message = "fixture-private-secret" }, HttpStatusCode.Forbidden))))
        {
            bool safe = false;
            try { await new BuddyOAuth(http, Providers.WorkBuddy).RefreshAsync(account, CancellationToken.None); }
            catch (UpstreamException ex) { safe = ex.Status == 401 && !ex.Message.Contains("fixture-private-secret"); }
            Check(safe, "Rejected refresh must require login and must not reflect raw upstream messages.");
        }
        using (var http = new HttpClient(new MockHandler(_ => throw new Exception("Expired or mismatched credentials must not be sent upstream."))))
        {
            account.BuddyRefreshExpiresAt = 1;
            await RejectAsync(() => new BuddyOAuth(http, Providers.WorkBuddy).RefreshAsync(account, CancellationToken.None), 401);
            checks++;
            bool rejected = false;
            try { await new BuddyOAuth(http, Providers.CodeBuddy).RefreshAsync(account, CancellationToken.None); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "Credentials must not cross domestic and international endpoints.");
        }

        int refreshes = 0, saved = 0;
        account = Fixture(); account.TokenExpiresAt = 1;
        using (var http = new HttpClient(new MockHandler(_ =>
        {
            refreshes++;
            return Json(new { code = 0, data = new { accessToken = "fixture-concurrent-access", refreshToken = "fixture-concurrent-refresh", expiresIn = 3600 } });
        })))
        {
            var client = new BuddyClient(http, account, (_, tokens) => { saved++; Check(tokens.RefreshToken == "fixture-concurrent-refresh", "Persist must receive the rotated credential."); });
            await Task.WhenAll(client.EnsureAccessAsync(CancellationToken.None), client.EnsureAccessAsync(CancellationToken.None));
            Check(refreshes == 1 && saved == 1, "Concurrent refreshes must be serialized and persist the new token once.");
        }
        refreshes = 0; saved = 0; account = Fixture(); account.TokenExpiresAt = 1;
        using (var http = new HttpClient(new MockHandler(_ =>
        {
            refreshes++;
            return Json(new { code = 0, data = new { accessToken = "fixture-unsaved-access", refreshToken = "fixture-unsaved-refresh", expiresIn = 3600 } });
        })))
        {
            var client = new BuddyClient(http, account, (_, _) => { if (++saved == 1) throw new IOException("fixture-disk-failure"); });
            await RejectAsync(() => client.EnsureAccessAsync(CancellationToken.None), 503);
            await client.EnsureAccessAsync(CancellationToken.None);
            Check(refreshes == 1 && saved == 2 && account.RefreshToken == "fixture-unsaved-refresh", "A failed credential save must be retried before use without rotating again.");
        }
        foreach (string provider in new[] { Providers.CodeBuddy, Providers.WorkBuddy })
        {
            account = Fixture(); account.Provider = provider; account.BuddyDomain = BuddyProduct.ForProvider(provider).Host;
            using var http = new HttpClient(new MockHandler(request =>
            {
                Check(request.RequestUri!.Host == BuddyProduct.ForProvider(provider).Host, "Catalog and balance queries must use the account's selected region.");
                if (request.RequestUri.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
                    return Json(new { code = 0, data = new { models = new object[] { new { id = "deepseek-v4.1-flash", name = "DeepSeek", maxOutputTokens = 128000, reasoning = new { supportedEfforts = new[] { "low", "high" }, defaultEffort = "high" } }, new { id = "completion-fixture", name = "Completion", maxOutputTokens = 100 } } } });
                if (request.RequestUri.AbsolutePath == "/v3/config")
                    return Json(new { code = 0, data = new { models = new object[] { new { id = "deepseek-v4.1-flash", name = "fallback", maxOutputTokens = 32000 }, new { id = "fixture-regional-model", name = "Regional", maxOutputTokens = 64000 } }, modelTiers = new object[]
                    {
                        new { enabled = true, priority = 1, modelIds = new[] { "fixture-regional-model", "deepseek-v4.1-flash" }, badge = new { label = "Pro Only" } },
                        new { enabled = false, priority = 9, modelIds = new[] { "fixture-regional-model" }, badge = new { label = "Disabled tier" } },
                        new { enabled = true, priority = 5, modelIds = new[] { "fixture-regional-model" }, badge = new { label = "Higher tier" } }
                    } } });
                if (request.RequestUri.AbsolutePath == "/v2/billing/meter/get-user-resource")
                    return Json(new { code = 0, data = new { Response = new { Data = new { Accounts = new object[] { new { CapacityUnit = "credit", Status = 0, CycleCapacityRemainPrecise = "55.67000031", CycleCapacityRemain = 55, CapacityRemain = 999999 }, new { CapacityUnit = "credit", Status = 3, CycleCapacityRemain = 500 }, new { CapacityUnit = "credit", Status = 0, CycleCapacityRemain = 100, DeductionEndTime = 1 }, new { CapacityUnit = "token", Status = 0, CycleCapacityRemain = 1234 } } } } } });
                throw new Exception("Unexpected catalog/balance request.");
            }));
            var client = new BuddyClient(http, account);
            var models = await client.FetchModelsAsync(CancellationToken.None);
            Check(models.Count == 2 && models.Single(m => m.Id == "deepseek-v4.1-flash").MaxOutputTokens == 128000 && models.Any(m => m.Id == "fixture-regional-model"), "Catalogs must merge by model ID, prefer scoped metadata, and remove completion-only models.");
            Check(models.Single(m => m.Id == "deepseek-v4.1-flash").Tier == "Pro Only" && models.Single(m => m.Id == "fixture-regional-model").Tier == "Higher tier", "Plan tiers from /v3/config must attach to merged models, ignore disabled tiers and prefer the highest priority.");
            var balance = await client.FetchBalanceAsync(CancellationToken.None);
            Check(balance.Credits == 55.67m && balance.ActivePackages == 1, "Balance must use precise current-cycle credits and exclude expired packages and other units.");
        }
        using (var http = new HttpClient(new MockHandler(_ => Json(new { code = 0, data = new { } }))))
        {
            await RejectAsync(() => new BuddyClient(http, Fixture()).FetchBalanceAsync(CancellationToken.None), 502);
            checks++;
        }
        var chat = new System.Text.Json.Nodes.JsonObject { ["model"] = "gemini-3.8-flash", ["messages"] = new System.Text.Json.Nodes.JsonArray() };
        foreach (var refusal in new Func<HttpResponseMessage>[]
        {
            () => Json(new { code = 11158, msg = "model [gemini-3.8-flash] is not allowed for user type [personal_free_user]" }, HttpStatusCode.BadRequest),
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {\"code\":11158,\"msg\":\"model [gemini-3.8-flash] is not allowed for user type [personal_free_user]\"}\n\n", Encoding.UTF8, "text/event-stream") }
        })
        {
            using var http = new HttpClient(new MockHandler(_ => refusal()));
            UpstreamException? planError = null;
            try { await foreach (var _ in new BuddyClient(http, Fixture()).EventsAsync(chat, CancellationToken.None)) { } }
            catch (UpstreamException ex) { planError = ex; }
            Check(planError is { Status: 403 } && planError.Message.Contains("套餐", StringComparison.Ordinal) && planError.Message.Contains("11158", StringComparison.Ordinal) && !planError.Message.Contains("personal_free_user", StringComparison.Ordinal),
                "Code 11158 must be reported as the account plan refusing the model, whether it arrives as an HTTP error or a stream frame, without echoing the upstream body.");
        }
        using (var http = new HttpClient(new MockHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("<html>fixture-private-secret</html>") })))
        {
            await RejectAsync(() => new BuddyClient(http, Fixture()).FetchBalanceAsync(CancellationToken.None), 429);
            checks++;
        }
        var past = DateTimeOffset.UtcNow.AddDays(-2);
        object[] expiredValues =
        [
            past.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            past.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            past.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            past.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            past.ToUnixTimeSeconds(), past.ToUnixTimeMilliseconds(),
            past.ToOffset(TimeSpan.FromHours(-7)).ToString("O", CultureInfo.InvariantCulture)
        ];
        foreach (object expired in expiredValues)
        {
            var future = DateTimeOffset.UtcNow.AddHours(1);
            using var http = new HttpClient(new MockHandler(_ => Json(new
            {
                code = 0, data = new { Response = new { Data = new { Accounts = new object[]
                {
                    new { CapacityUnit = "credit", Status = 0, CycleCapacityRemain = 100, ExpiredTime = expired },
                    new { CapacityUnit = "credits", Status = 0, CycleCapacityRemain = 7, ExpiredTime = future.ToOffset(TimeSpan.FromHours(-14)).ToString("O", CultureInfo.InvariantCulture) }
                } } } }
            })));
            var balance = await new BuddyClient(http, Fixture()).FetchBalanceAsync(CancellationToken.None);
            Check(balance.Credits == 7m && balance.ActivePackages == 1, "Expired date-only, Beijing wall-clock, Unix second/millisecond and explicitly offset packages must be excluded; explicit offsets must retain future credit.");
        }
        foreach (string invalidExpiry in new[] { "not-a-date", "999999999999999999999", "2026-02-30", "0001-01-01" })
        {
            using var http = new HttpClient(new MockHandler(_ => Json(new { code = 0, data = new { Response = new { Data = new { Accounts = new[] { new { CapacityUnit = "credit", Status = 0, CycleCapacityRemain = 100, ExpiredTime = invalidExpiry } } } } } })));
            await RejectAsync(() => new BuddyClient(http, Fixture()).FetchBalanceAsync(CancellationToken.None), 502);
            checks++;
        }
        var parsePackageDate = typeof(BuddyClient).GetMethod("TryPackageDate", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        foreach (var example in new[]
        {
            (Raw: "2026-11-01", Utc: "2026-10-31T16:00:00Z"),
            (Raw: "2026-11-01 00:00", Utc: "2026-10-31T16:00:00Z"),
            (Raw: "2026-11-01T00:00:00.123", Utc: "2026-10-31T16:00:00.123Z"),
            (Raw: "2026-11-01T00:00:00-07:00", Utc: "2026-11-01T07:00:00Z")
        })
        {
            object?[] values = [example.Raw, default(DateTimeOffset)];
            bool parsed = (bool)parsePackageDate.Invoke(null, values)!;
            Check(parsed && (DateTimeOffset)values[1]! == DateTimeOffset.Parse(example.Utc, CultureInfo.InvariantCulture), "Package dates must map to the expected instant independently of the host timezone, retaining explicit offsets.");
        }
        return checks;
    }

    private static Account Fixture() => new()
    {
        Provider = Providers.WorkBuddy, AccessToken = "fixture-access-only", RefreshToken = "fixture-refresh-only",
        TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), BuddyUserId = "fixture-user",
        BuddyDomain = "www.workbuddy.ai", BuddyAccountType = "personal"
    };
    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static async Task RejectAsync(Func<Task> action, int status)
    {
        try { await action(); }
        catch (UpstreamException ex) when (ex.Status == status) { return; }
        throw new Exception($"Expected Buddy HTTP {status} rejection.");
    }
    private sealed class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
