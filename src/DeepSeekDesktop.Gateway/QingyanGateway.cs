using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeepSeekDesktop.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DeepSeekDesktop.Gateway;

public sealed class QingyanGateway : IAsyncDisposable
{
    private sealed class Slot(QingyanClient client)
    {
        public QingyanClient Client { get; } = client;
        public int State; // 0 ready, 1 busy, 2 transport/protocol error, 3 invalid auth
        public Queue<DateTimeOffset> Requests { get; } = new();
    }
    private readonly Settings settings;
    private readonly HttpClient http;
    private readonly List<Slot> slots;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Action<string>? log;
    private WebApplication? app;
    private bool ready;
    private int pending, cursor, total, success, failed;
    private DateTimeOffset lastStart;
    public bool IsRunning => app != null && !lifetime.IsCancellationRequested;

    public QingyanGateway(Settings settings, Action<string, string>? rotated = null, Action<string>? log = null, HttpMessageHandler? handler = null)
    {
        settings.Validate();
        this.settings = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
        this.log = log;
        handler ??= new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = settings.ProxyUrl.Length > 0, Proxy = settings.ProxyUrl.Length > 0 ? new WebProxy(settings.ProxyUrl) : null };
        http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(3), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
        slots = this.settings.Accounts.Where(a => a.Enabled && a.Provider == Providers.Qingyan).Select(a => new Slot(new QingyanClient(http, a, rotated))).ToList();
    }

    public async Task StartAsync(CancellationToken cancellation = default)
    {
        if (app != null) throw new InvalidOperationException("清言服务已启动。");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders(); // Never log request bodies, auth headers or upstream credentials.
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, settings.Port);
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 1024 * 1024;
            options.Limits.MaxConcurrentConnections = 32;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        });
        app = builder.Build();
        app.Run(HandleAsync);
        await app.StartAsync(cancellation);
        // Bind the local port before any credential is transmitted. Startup validates tokens, sends no chat.
        foreach (var slot in slots)
        {
            try { await slot.Client.CheckAsync(cancellation); log?.Invoke("清言登录态校验通过；真实对话仍需单独测试。"); }
            catch (UpstreamException ex) { Volatile.Write(ref slot.State, ex.Status == 401 ? 3 : 2); log?.Invoke(ex.Message); }
            catch (Exception ex) when (ex is HttpRequestException or JsonException) { Volatile.Write(ref slot.State, 2); log?.Invoke("清言登录态校验失败，请检查网络或网页账号状态。"); }
        }
        ready = true;
        log?.Invoke($"清言本地 API 已启动：{settings.BaseUrl}");
    }

    public JsonElement Status(string resource) => resource switch
    {
        "stats" => JsonSerializer.SerializeToElement(new { total_requests = Volatile.Read(ref total), success_requests = Volatile.Read(ref success), failed_requests = Volatile.Read(ref failed) }),
        "status" => JsonSerializer.SerializeToElement(new { idle = slots.Count(s => Volatile.Read(ref s.State) == 0), busy = slots.Count(s => Volatile.Read(ref s.State) == 1), error = slots.Count(s => Volatile.Read(ref s.State) == 2), invalid = slots.Count(s => Volatile.Read(ref s.State) == 3) }),
        _ => throw new ArgumentException("不支持的状态资源。")
    };

    private bool Authorized(HttpContext context)
    {
        var supplied = Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString());
        var expected = Encoding.UTF8.GetBytes("Bearer " + settings.ApiKey);
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    private async Task HandleAsync(HttpContext context)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.Token);
        cancellation.CancelAfter(TimeSpan.FromMinutes(3));
        bool acquired = false, counted = false;
        Slot? selected = null;
        try
        {
            if (!Authorized(context)) { await ErrorAsync(context, 401, "本地 API Key 无效。", cancellation.Token); return; }
            // No CORS middleware and no public administrative routes.
            if (context.Request.Headers.ContainsKey("Origin")) { await ErrorAsync(context, 403, "不允许网页跨源访问本地 API。", cancellation.Token); return; }
            if (!ready) { await ErrorAsync(context, 503, "清言登录态正在校验，请稍候。", cancellation.Token); return; }
            if (context.Request.Method == "GET" && context.Request.Path == "/v1/models")
            {
                await context.Response.WriteAsJsonAsync(new { @object = "list", data = new[] { new { id = "qingyan-default", @object = "model", owned_by = "qingyan-web" } } }, cancellation.Token); return;
            }
            if (context.Request.Method != "POST" || context.Request.Path != "/v1/chat/completions") { await ErrorAsync(context, 404, "不支持的接口。", cancellation.Token); return; }
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellation.Token);
            var input = body.RootElement;
            var tools = QingyanTools.Prepare(input);
            string prompt = tools.Prompt;
            bool stream = input.TryGetProperty("stream", out var streaming) && streaming.GetBoolean();
            bool search = settings.SearchEnabled;
            if (input.TryGetProperty("web_search_options", out var searchOptions) && searchOptions.ValueKind == JsonValueKind.Object && searchOptions.TryGetProperty("search_context_size", out var searchSize)) search = searchSize.ToString() != "none";
            if (tools.Enabled) search = false; // External tools belong to the client; do not substitute built-in search.
            if (Interlocked.Increment(ref pending) > 8) { Interlocked.Decrement(ref pending); await ErrorAsync(context, 429, "清言请求队列已满，请稍后重试。", cancellation.Token); return; }
            try { await gate.WaitAsync(cancellation.Token); acquired = true; }
            finally { Interlocked.Decrement(ref pending); }
            for (int i = 0; i < slots.Count; i++)
            {
                var slot = slots[(cursor + i) % slots.Count];
                while (slot.Requests.TryPeek(out var time) && time <= DateTimeOffset.UtcNow.AddHours(-1)) slot.Requests.Dequeue();
                if (Volatile.Read(ref slot.State) != 3 && slot.Requests.Count < settings.HourlyQuota) { selected = slot; cursor = (cursor + i + 1) % slots.Count; break; }
            }
            if (selected == null) { await ErrorAsync(context, slots.All(s => Volatile.Read(ref s.State) == 3) ? 503 : 429, "清言账号不可用或已达到本机每小时请求上限。", cancellation.Token); return; }
            var delay = lastStart.AddMilliseconds(1500) - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellation.Token);
            lastStart = DateTimeOffset.UtcNow;
            selected.Requests.Enqueue(lastStart);
            Volatile.Write(ref selected.State, 1);
            Interlocked.Increment(ref total); counted = true;
            string id = "chatcmpl-" + Guid.NewGuid().ToString("N");
            long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var text = new StringBuilder(); var reasoning = new StringBuilder();
            bool first = true;
            await foreach (var delta in selected.Client.ChatAsync(prompt, search, cancellation.Token, tools.Enabled))
            {
                text.Append(delta.Text); reasoning.Append(delta.Reasoning);
                if (text.Length + reasoning.Length > 1024 * 1024) throw new UpstreamException(502, "清言回复过长，已中止本次请求。");
                // Buffer tool-enabled replies until the complete envelope and arguments are validated.
                if (!stream || tools.Enabled) continue;
                if (first)
                {
                    context.Response.ContentType = "text/event-stream; charset=utf-8";
                    context.Response.Headers.CacheControl = "no-cache";
                    await ChunkAsync(context, id, created, new { role = "assistant", content = "" }, null, cancellation.Token); first = false;
                }
                if (delta.Reasoning.Length > 0) await ChunkAsync(context, id, created, new { reasoning_content = delta.Reasoning }, null, cancellation.Token);
                if (delta.Text.Length > 0) await ChunkAsync(context, id, created, new { content = delta.Text }, null, cancellation.Token);
            }
            var calls = tools.Parse(text.ToString());
            string finish = calls.Count > 0 ? "tool_calls" : "stop";
            if (stream)
            {
                if (tools.Enabled)
                {
                    context.Response.ContentType = "text/event-stream; charset=utf-8";
                    context.Response.Headers.CacheControl = "no-cache";
                    await ChunkAsync(context, id, created, new { role = "assistant", content = "" }, null, cancellation.Token);
                    if (reasoning.Length > 0) await ChunkAsync(context, id, created, new { reasoning_content = reasoning.ToString() }, null, cancellation.Token);
                    if (calls.Count == 0) await ChunkAsync(context, id, created, new { content = text.ToString() }, null, cancellation.Token);
                    else for (int i = 0; i < calls.Count; i++)
                    {
                        var call = calls[i];
                        await ChunkAsync(context, id, created, new { tool_calls = new[] { new { index = i, id = call.Id, type = "function", function = new { name = call.Name, arguments = call.Arguments } } } }, null, cancellation.Token);
                    }
                }
                await ChunkAsync(context, id, created, new { }, finish, cancellation.Token);
                await context.Response.WriteAsync("data: [DONE]\n\n", cancellation.Token);
            }
            else
            {
                var message = new System.Collections.Generic.Dictionary<string, object?> { ["role"] = "assistant", ["content"] = calls.Count > 0 ? null : text.ToString(), ["reasoning_content"] = reasoning.ToString() };
                if (calls.Count > 0) message["tool_calls"] = calls.Select(c => new { id = c.Id, type = "function", function = new { name = c.Name, arguments = c.Arguments } }).ToArray();
                await context.Response.WriteAsJsonAsync(new { id, @object = "chat.completion", created, model = "qingyan-default", choices = new[] { new { index = 0, message, finish_reason = finish } } }, cancellation.Token);
            }
            Interlocked.Increment(ref success); Volatile.Write(ref selected.State, 0);
        }
        catch (Exception ex)
        {
            if (counted) Interlocked.Increment(ref failed);
            if (selected != null) Volatile.Write(ref selected.State, ex is UpstreamException { Status: 401 } ? 3 : ex is OperationCanceledException ? 0 : 2);
            if (context.RequestAborted.IsCancellationRequested || lifetime.IsCancellationRequested) return;
            int status = ex switch { UpstreamException upstream => upstream.Status, BadHttpRequestException bad => bad.StatusCode, JsonException or ArgumentException or InvalidOperationException => 400, OperationCanceledException => 504, _ => 502 };
            string message = ex switch { UpstreamException or ArgumentException => ex.Message, JsonException or InvalidOperationException => "请求 JSON 或字段格式无效。", BadHttpRequestException => "请求体无效或超过 1 MB。", OperationCanceledException => "清言响应超时。", _ => "清言连接失败，请检查网络或网页账号状态。" };
            if (counted) log?.Invoke(message);
            try { await ErrorAsync(context, status, message, context.RequestAborted); } catch (Exception failure) when (failure is OperationCanceledException or IOException) { }
        }
        finally { if (acquired) gate.Release(); }
    }

    public static string ParsePrompt(JsonElement input) => QingyanTools.Prepare(input).Prompt;

    private static async Task ChunkAsync(HttpContext context, string id, long created, object delta, string? finish, CancellationToken cancellation)
    {
        await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(new { id, @object = "chat.completion.chunk", created, model = "qingyan-default", choices = new[] { new { index = 0, delta, finish_reason = finish } } }) + "\n\n", cancellation);
        await context.Response.Body.FlushAsync(cancellation);
    }

    private static async Task ErrorAsync(HttpContext context, int status, string message, CancellationToken cancellation)
    {
        var body = new { error = new { message, type = "qingyan_error", code = status } };
        if (context.Response.HasStarted)
        {
            await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(body) + "\n\n", cancellation);
            await context.Response.Body.FlushAsync(cancellation);
        }
        else { context.Response.StatusCode = status; await context.Response.WriteAsJsonAsync(body, cancellation); }
    }

    public async ValueTask DisposeAsync()
    {
        ready = false; lifetime.Cancel();
        if (app != null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await app.StopAsync(timeout.Token); }
            finally { await app.DisposeAsync(); app = null; http.Dispose(); }
        }
        else http.Dispose();
    }
}
