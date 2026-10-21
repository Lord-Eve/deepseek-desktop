using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeepSeekDesktop.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DeepSeekDesktop.Gateway;

public sealed class GrokGateway : IAsyncDisposable
{
    private readonly Settings settings;
    private readonly HttpClient http;
    private readonly GrokClient client;
    private readonly Action<string>? log;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Queue<DateTimeOffset> requests = new();
    private WebApplication? app;
    private bool ready;
    private int state, total, success, failed, waiting;
    public bool IsRunning => app != null && !lifetime.IsCancellationRequested;
    public GrokGateway(Settings settings, Action<string, GrokTokens>? persist = null, Action<string>? log = null, HttpMessageHandler? handler = null)
    {
        settings.Validate();
        if (settings.Provider != Providers.Grok) throw new ArgumentException("Grok 服务需要选择 Grok 平台。");
        this.settings = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!; this.log = log;
        handler ??= new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = settings.ProxyUrl.Length > 0, Proxy = settings.ProxyUrl.Length > 0 ? new WebProxy(settings.ProxyUrl) : null };
        http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(3) };
        client = new(http, this.settings.Accounts.Single(a => a.Provider == Providers.Grok && a.Enabled), persist);
    }
    public async Task StartAsync(CancellationToken cancellation = default)
    {
        if (app != null) throw new InvalidOperationException("Grok 服务已启动。");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory }); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => { options.Listen(IPAddress.Loopback, settings.Port); options.AddServerHeader = false; options.Limits.MaxRequestBodySize = 1024 * 1024; options.Limits.MaxConcurrentConnections = 32; options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10); });
        app = builder.Build(); app.Run(HandleAsync); await app.StartAsync(cancellation);
        await client.EnsureAccessAsync(cancellation);
        ready = true; log?.Invoke("Grok 本地 API 已启动，授权已载入；真实模型权限与订阅额度请单独测试。");
    }
    public JsonElement Status(string resource) => resource switch
    {
        "stats" => JsonSerializer.SerializeToElement(new { total_requests = Volatile.Read(ref total), success_requests = Volatile.Read(ref success), failed_requests = Volatile.Read(ref failed) }),
        "status" => JsonSerializer.SerializeToElement(new { idle = state == 0 ? 1 : 0, busy = state == 1 ? 1 : 0, error = state == 2 ? 1 : 0, invalid = state == 3 ? 1 : 0 }),
        _ => throw new ArgumentException("不支持的状态资源。")
    };
    private async Task HandleAsync(HttpContext context)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.Token); cancellation.CancelAfter(TimeSpan.FromMinutes(3));
        bool acquired = false, counted = false;
        try
        {
            byte[] supplied = Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()), expected = Encoding.UTF8.GetBytes("Bearer " + settings.ApiKey);
            if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected)) { await ErrorAsync(context, 401, "本地 API Key 无效。", cancellation.Token); return; }
            if (context.Request.Headers.ContainsKey("Origin")) { await ErrorAsync(context, 403, "不允许网页跨源访问本地 API。", cancellation.Token); return; }
            if (!ready) { await ErrorAsync(context, 503, "Grok 服务尚未就绪。", cancellation.Token); return; }
            if (context.Request.Method == "GET" && context.Request.Path == "/v1/models") { await context.Response.WriteAsJsonAsync(new { @object = "list", data = GrokProtocol.Models.Select(id => new { id, @object = "model", owned_by = "xai-cli-preset" }).ToArray() }, cancellation.Token); return; }
            bool native = context.Request.Path == "/v1/responses";
            if (context.Request.Method != "POST" || !native && context.Request.Path != "/v1/chat/completions") { await ErrorAsync(context, 404, "不支持的接口。", cancellation.Token); return; }
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellation.Token);
            var input = document.RootElement; bool stream = GrokProtocol.Streaming(input);
            if (Volatile.Read(ref state) == 3) { await ErrorAsync(context, 503, "Grok 授权或订阅资格已被拒绝，请停止服务并重新登录或选择有权限的模型。", cancellation.Token); return; }
            if (Interlocked.Increment(ref waiting) > 8) { Interlocked.Decrement(ref waiting); await ErrorAsync(context, 429, "Grok 请求队列已满。", cancellation.Token); return; }
            try { await gate.WaitAsync(cancellation.Token); acquired = true; } finally { Interlocked.Decrement(ref waiting); }
            // Conversion and replay cache access are serialized with upstream requests.
            var request = native ? GrokProtocol.Native(input) : GrokProtocol.Chat(input, client.Replay);
            while (requests.TryPeek(out var old) && old <= DateTimeOffset.UtcNow.AddHours(-1)) requests.Dequeue();
            if (requests.Count >= settings.HourlyQuota) { await ErrorAsync(context, 429, "已达到本机每账号每小时请求上限。", cancellation.Token); return; }
            requests.Enqueue(DateTimeOffset.UtcNow); Interlocked.Increment(ref total); counted = true; Volatile.Write(ref state, 1);
            string id = "chatcmpl-" + Guid.NewGuid().ToString("N"), model = request["model"]!.ToString(); long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string emittedText = "", emittedReasoning = ""; bool started = false; GrokCompletion? completed = null;
            async Task StartStream()
            {
                if (started) return; started = true; context.Response.ContentType = "text/event-stream; charset=utf-8"; context.Response.Headers.CacheControl = "no-cache";
                if (!native) await ChunkAsync(context, id, created, model, new { role = "assistant", content = "" }, null, cancellation.Token);
            }
            await foreach (var frame in client.EventsAsync(request, cancellation.Token))
            {
                string? type = frame["type"]?.ToString();
                if (type == "response.completed")
                {
                    if (frame["response"] is not JsonObject response) throw new UpstreamException(502, "Grok 完成事件缺少回复对象。");
                    try { completed = GrokProtocol.Complete(request, response); }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException) { throw new UpstreamException(502, "Grok 完成事件格式无效，请检查上游协议是否变化。"); }
                    if (!native) client.Replay.Save(request, completed);
                }
                if (!stream) continue;
                if (native) { await StartStream(); await context.Response.WriteAsync("data: " + frame.ToJsonString() + "\n\n", cancellation.Token); await context.Response.Body.FlushAsync(cancellation.Token); continue; }
                if (type == "response.output_text.delta")
                {
                    string delta = frame["delta"]?.ToString() ?? ""; emittedText += delta;
                    if (emittedText.Length > 1024 * 1024) throw new UpstreamException(502, "Grok 回复过长。");
                    await StartStream(); await ChunkAsync(context, id, created, model, new { content = delta }, null, cancellation.Token);
                }
                if (type is "response.reasoning_summary_text.delta" or "response.reasoning_text.delta")
                {
                    string delta = frame["delta"]?.ToString() ?? ""; emittedReasoning += delta;
                    if (emittedReasoning.Length > 1024 * 1024) throw new UpstreamException(502, "Grok 思考输出过长。");
                    await StartStream(); await ChunkAsync(context, id, created, model, new { reasoning_content = delta }, null, cancellation.Token);
                }
            }
            if (completed == null) throw new UpstreamException(502, "Grok 缺少完整回复。");
            if (native) { if (!stream) await context.Response.WriteAsJsonAsync(completed.Response, cancellation.Token); }
            else
            {
                string finish = completed.Calls.Count > 0 ? "tool_calls" : "stop";
                if (stream)
                {
                    await StartStream();
                    if (completed.Text.Length > 0)
                    {
                        if (!completed.Text.StartsWith(emittedText, StringComparison.Ordinal)) throw new UpstreamException(502, "Grok 流式正文与完成快照不一致。");
                        if (completed.Text.Length > emittedText.Length) await ChunkAsync(context, id, created, model, new { content = completed.Text[emittedText.Length..] }, null, cancellation.Token);
                    }
                    if (emittedReasoning.Length == 0 && completed.Reasoning.Length > 0) await ChunkAsync(context, id, created, model, new { reasoning_content = completed.Reasoning }, null, cancellation.Token);
                    for (int i = 0; i < completed.Calls.Count; i++) { var call = completed.Calls[i]; await ChunkAsync(context, id, created, model, new { tool_calls = new[] { new { index = i, id = call.Id, type = "function", function = new { name = call.Name, arguments = call.Arguments } } } }, null, cancellation.Token); }
                    await ChunkAsync(context, id, created, model, new { }, finish, cancellation.Token); await context.Response.WriteAsync("data: [DONE]\n\n", cancellation.Token);
                }
                else
                {
                    var message = new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = completed.Text.Length == 0 && completed.Calls.Count > 0 ? null : completed.Text, ["reasoning_content"] = completed.Reasoning };
                    if (completed.Calls.Count > 0) message["tool_calls"] = completed.Calls.Select(c => new { id = c.Id, type = "function", function = new { name = c.Name, arguments = c.Arguments } }).ToArray();
                    var result = new Dictionary<string, object?> { ["id"] = id, ["object"] = "chat.completion", ["created"] = created, ["model"] = model, ["choices"] = new[] { new { index = 0, message, finish_reason = finish } } };
                    if (completed.Response["usage"] is JsonObject usage) result["usage"] = new { prompt_tokens = usage["input_tokens"]?.GetValue<long>() ?? 0, completion_tokens = usage["output_tokens"]?.GetValue<long>() ?? 0, total_tokens = usage["total_tokens"]?.GetValue<long>() ?? 0 };
                    await context.Response.WriteAsJsonAsync(result, cancellation.Token);
                }
            }
            Interlocked.Increment(ref success); Volatile.Write(ref state, 0);
        }
        catch (Exception ex)
        {
            if (counted) { Interlocked.Increment(ref failed); Volatile.Write(ref state, ex is UpstreamException { Status: 401 } ? 3 : ex is OperationCanceledException ? 0 : 2); }
            if (context.RequestAborted.IsCancellationRequested || lifetime.IsCancellationRequested) return;
            int status = ex switch { UpstreamException u => u.Status, BadHttpRequestException bad => bad.StatusCode, JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException => 400, OperationCanceledException => 504, _ => 502 };
            string message = ex switch { UpstreamException or ArgumentException => ex.Message, OperationCanceledException => "Grok 请求超时。", JsonException or InvalidOperationException or KeyNotFoundException => "请求字段或 JSON 格式无效。", BadHttpRequestException => "请求体无效或超过 1 MB。", _ => "Grok 连接失败，请检查网络。" };
            if (counted) log?.Invoke(message);
            try { await ErrorAsync(context, status, message, context.RequestAborted); } catch (Exception failure) when (failure is IOException or OperationCanceledException) { }
        }
        finally { if (acquired) gate.Release(); }
    }
    private static async Task ChunkAsync(HttpContext context, string id, long created, string model, object delta, string? finish, CancellationToken cancellation)
    {
        await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(new { id, @object = "chat.completion.chunk", created, model, choices = new[] { new { index = 0, delta, finish_reason = finish } } }) + "\n\n", cancellation); await context.Response.Body.FlushAsync(cancellation);
    }
    private static async Task ErrorAsync(HttpContext context, int status, string message, CancellationToken cancellation)
    {
        var error = new { error = new { message, type = "grok_error", code = status } };
        if (context.Response.HasStarted) { await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(error) + "\n\n", cancellation); await context.Response.Body.FlushAsync(cancellation); }
        else { context.Response.StatusCode = status; await context.Response.WriteAsJsonAsync(error, cancellation); }
    }
    public async ValueTask DisposeAsync()
    {
        ready = false; lifetime.Cancel();
        if (app != null) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); try { await app.StopAsync(timeout.Token); } finally { await app.DisposeAsync(); app = null; } }
        client.Replay.Clear(); http.Dispose();
    }
}
