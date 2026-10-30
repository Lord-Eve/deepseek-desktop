using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DeepSeekDesktop.Core;
using DeepSeekDesktop.Gateway;

internal static class BuddyQueueChecks
{
    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
        var settings = Fixture();
        var handler = new DelayedBuddy(TimeSpan.FromMilliseconds(650));
        await using (var gateway = new BuddyGateway(settings, handler: handler, requestTimeout: TimeSpan.FromSeconds(1), queueTimeout: TimeSpan.FromSeconds(3)))
        {
            await gateway.StartAsync();
            using var api = Api(settings);
            var first = SendAsync(api);
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = SendAsync(api);
            using var firstResponse = await first;
            using var secondResponse = await second;
            Check(firstResponse.IsSuccessStatusCode && secondResponse.IsSuccessStatusCode && handler.Calls == 2,
                "A queued call must receive its full execution window after the previous call finishes.");
            Check(gateway.Status("stats").GetProperty("failed_requests").GetInt32() == 0,
                "Queue wait must not turn a valid second completion into an upstream timeout.");
        }

        settings = Fixture();
        handler = new DelayedBuddy(Timeout.InfiniteTimeSpan);
        await using (var gateway = new BuddyGateway(settings, handler: handler, requestTimeout: TimeSpan.FromSeconds(5), queueTimeout: TimeSpan.FromMilliseconds(150)))
        {
            await gateway.StartAsync();
            using var api = Api(settings);
            var first = SendAsync(api);
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var queued = await SendAsync(api);
            string error = await queued.Content.ReadAsStringAsync();
            Check(queued.StatusCode == HttpStatusCode.GatewayTimeout && handler.Calls == 1 && error.Contains("排队", StringComparison.Ordinal),
                "An expired queue wait must fail locally before making another upstream call.");
            Check(gateway.Status("stats").GetProperty("total_requests").GetInt32() == 1,
                "A queued request that never reaches upstream must not consume the local request count.");
            handler.Release.TrySetResult();
            using var completed = await first;
            Check(completed.IsSuccessStatusCode, "Queue timeout must not cancel the active request.");
        }
        return checks;
    }

    private static Settings Fixture()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        return new Settings
        {
            Provider = Providers.CodeBuddy, Port = port,
            Accounts = [new Account { Provider = Providers.CodeBuddy, AccessToken = "fixture-queue-access-only", RefreshToken = "fixture-queue-refresh-only", TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), BuddyDomain = "copilot.tencent.com" }]
        };
    }
    private static HttpClient Api(Settings settings)
    {
        var api = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(settings.BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(10) };
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        return api;
    }
    private static Task<HttpResponseMessage> SendAsync(HttpClient api) => api.PostAsync("chat/completions", new StringContent("""{"model":"deepseek-v4.1-flash","messages":[{"role":"user","content":"fixture queue"}]}""", Encoding.UTF8, "application/json"));
    private sealed class DelayedBuddy(TimeSpan delay) : HttpMessageHandler
    {
        public int Calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            if (request.RequestUri!.AbsolutePath is "/v3/config" or "/console/enterprises/personal/models")
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { code = 0, data = new { models = new[] { new { id = "deepseek-v4.1-flash", maxOutputTokens = 128000 } } } }), Encoding.UTF8, "application/json") };
            if (request.RequestUri.AbsolutePath != "/v2/chat/completions") throw new Exception("Unexpected queue-check request.");
            Interlocked.Increment(ref Calls); Entered.TrySetResult();
            if (delay == Timeout.InfiniteTimeSpan) await Release.Task.WaitAsync(cancellation);
            else await Task.Delay(delay, cancellation);
            const string output = "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":null}]}\n\ndata: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
            return new(HttpStatusCode.OK) { Content = new StringContent(output, Encoding.UTF8, "text/event-stream") };
        }
    }
}
