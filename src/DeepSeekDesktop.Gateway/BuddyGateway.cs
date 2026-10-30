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

public sealed class BuddyGateway : IAsyncDisposable
{
    private readonly Settings settings;
    private readonly HttpClient http;
    private readonly BuddyClient client;
    private readonly Action<string>? log;
    private readonly TimeSpan requestTimeout, queueTimeout;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Queue<DateTimeOffset> requests = new();
    private WebApplication? app;
    private IReadOnlyList<BuddyModel> models = [];
    private bool ready;
    private int state, total, success, failed, waiting;
    public bool IsRunning => app != null && !lifetime.IsCancellationRequested;
    public BuddyGateway(Settings settings, Action<string, BuddyTokens>? persist = null, Action<string>? log = null, HttpMessageHandler? handler = null, TimeSpan? requestTimeout = null, TimeSpan? queueTimeout = null)
    {
        settings.Validate();
        if (settings.Provider is not (Providers.CodeBuddy or Providers.WorkBuddy)) throw new ArgumentException("Buddy 服务需要选择 CodeBuddy 国内版或 WorkBuddy 国际版。");
        this.settings = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!; this.log = log;
        this.requestTimeout = requestTimeout ?? TimeSpan.FromMinutes(3);
        this.queueTimeout = queueTimeout ?? TimeSpan.FromMinutes(3);
        if (this.requestTimeout <= TimeSpan.Zero || this.requestTimeout.TotalMilliseconds > uint.MaxValue - 1 || this.queueTimeout <= TimeSpan.Zero || this.queueTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), "请求和排队超时必须为有效的正时间。");
        handler ??= new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = settings.ProxyUrl.Length > 0, Proxy = settings.ProxyUrl.Length > 0 ? new WebProxy(settings.ProxyUrl) : null };
        http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(3) };
        client = new(http, this.settings.Accounts.Single(a => a.Provider == settings.Provider && a.Enabled), persist);
    }
    public async Task StartAsync(CancellationToken cancellation = default)
    {
        if (app != null || lifetime.IsCancellationRequested) throw new InvalidOperationException("Buddy 服务已启动或已停止，请重新创建服务。");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory }); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => { options.Listen(IPAddress.Loopback, settings.Port); options.AddServerHeader = false; options.Limits.MaxRequestBodySize = 1024 * 1024; options.Limits.MaxConcurrentConnections = 32; options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10); });
        app = builder.Build(); app.Run(HandleAsync);
        try
        {
            await app.StartAsync(cancellation);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
            await client.EnsureAccessAsync(linked.Token); models = await client.FetchModelsAsync(linked.Token);
            if (models.Count == 0) throw new UpstreamException(502, "Buddy 没有返回当前账号的可用模型，请确认授权和模型权限。");
            ready = true; log?.Invoke($"{settings.ProviderName} 本地 API 已启动，模型目录已载入；启动未发送对话。");
        }
        catch { await DisposeAsync(); throw; }
    }
    public JsonElement Status(string resource) => resource switch
    {
        "stats" => JsonSerializer.SerializeToElement(new { total_requests = Volatile.Read(ref total), success_requests = Volatile.Read(ref success), failed_requests = Volatile.Read(ref failed) }),
        "status" => JsonSerializer.SerializeToElement(new { idle = state == 0 ? 1 : 0, busy = state == 1 ? 1 : 0, error = state == 2 ? 1 : 0, invalid = state == 3 ? 1 : 0 }),
        _ => throw new ArgumentException("不支持的状态资源。")
    };
    public Task<BuddyBalance> BalanceAsync(CancellationToken cancellation = default) => client.FetchBalanceAsync(cancellation);
    private async Task HandleAsync(HttpContext context)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.Token); cancellation.CancelAfter(queueTimeout);
        bool acquired = false, counted = false;
        try
        {
            byte[] supplied = Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()), expected = Encoding.UTF8.GetBytes("Bearer " + settings.ApiKey);
            if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected)) { await ErrorAsync(context, 401, "本地 API Key 无效。", cancellation.Token); return; }
            if (context.Request.Headers.ContainsKey("Origin")) { await ErrorAsync(context, 403, "不允许网页跨源访问本地 API。", cancellation.Token); return; }
            if (!ready) { await ErrorAsync(context, 503, "Buddy 服务尚未就绪。", cancellation.Token); return; }
            if (context.Request.Method == "GET" && context.Request.Path == "/v1/models")
            {
                await context.Response.WriteAsJsonAsync(new { @object = "list", data = models.Select(m => new { id = m.Id, @object = "model", owned_by = settings.Provider, name = m.Name, max_input_tokens = m.MaxInputTokens, max_output_tokens = m.MaxOutputTokens, reasoning_efforts = m.ReasoningEfforts }).ToArray() }, cancellation.Token); return;
            }
            bool responses = context.Request.Path == "/v1/responses";
            if (context.Request.Method != "POST" || !responses && context.Request.Path != "/v1/chat/completions") { await ErrorAsync(context, 404, "不支持的接口。", cancellation.Token); return; }
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellation.Token);
            var input = document.RootElement;
            if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException("请求应为 JSON 对象。");
            bool stream = BuddyProtocol.Streaming(input);
            var request = responses ? BuddyProtocol.Responses(input, models) : BuddyProtocol.Chat(input, models);
            if (Volatile.Read(ref state) == 3) { await ErrorAsync(context, 503, "Buddy 授权已失效，请停止服务并重新登录。", cancellation.Token); return; }
            if (Interlocked.Increment(ref waiting) > 8) { Interlocked.Decrement(ref waiting); await ErrorAsync(context, 429, "Buddy 请求队列已满。", cancellation.Token); return; }
            try { await gate.WaitAsync(cancellation.Token); acquired = true; } finally { Interlocked.Decrement(ref waiting); }
            // Queuing must not consume the upstream request's execution window.
            cancellation.Token.ThrowIfCancellationRequested();
            cancellation.CancelAfter(requestTimeout);
            if (Volatile.Read(ref state) == 3) { await ErrorAsync(context, 503, "Buddy 授权已失效，请停止服务并重新登录。", cancellation.Token); return; }
            while (requests.TryPeek(out var old) && old <= DateTimeOffset.UtcNow.AddHours(-1)) requests.Dequeue();
            if (requests.Count >= settings.HourlyQuota) { await ErrorAsync(context, 429, "已达到本机每账号每小时请求上限。", cancellation.Token); return; }
            requests.Enqueue(DateTimeOffset.UtcNow); Interlocked.Increment(ref total); counted = true; Volatile.Write(ref state, 1);
            string id = Guid.NewGuid().ToString("N"), model = request["model"]!.ToString(); long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var result = new BuddyResult(); bool started = false;
            async Task StartStream()
            {
                if (started) return; started = true; context.Response.ContentType = "text/event-stream; charset=utf-8"; context.Response.Headers.CacheControl = "no-cache";
                if (!responses) await ChunkAsync(context, id, created, model, new { role = "assistant", content = "" }, null, cancellation.Token);
            }
            await foreach (var frame in client.EventsAsync(request, cancellation.Token))
            {
                var delta = result.Add(frame);
                if (!stream || responses || delta.Text.Length == 0 && delta.Reasoning.Length == 0) continue;
                await StartStream();
                if (delta.Text.Length > 0) await ChunkAsync(context, id, created, model, new { content = delta.Text }, null, cancellation.Token);
                if (delta.Reasoning.Length > 0) await ChunkAsync(context, id, created, model, new { reasoning_content = delta.Reasoning }, null, cancellation.Token);
            }
            var reply = result.Complete(request);
            if (responses)
            {
                var response = BuddyProtocol.Response(reply, id, model, created);
                response["parallel_tool_calls"] = request["parallel_tool_calls"]?.DeepClone() ?? JsonValue.Create(true);
                if (stream) { await StartStream(); await ResponsesStreamAsync(context, response, cancellation.Token); }
                else await context.Response.WriteAsJsonAsync(response, cancellation.Token);
            }
            else if (stream)
            {
                await StartStream();
                for (int i = 0; i < reply.Calls.Count; i++)
                {
                    var call = reply.Calls[i]; await ChunkAsync(context, id, created, model, new { tool_calls = new[] { new { index = i, id = call.Id, type = "function", function = new { name = call.Name, arguments = call.Arguments } } } }, null, cancellation.Token);
                }
                await ChunkAsync(context, id, created, model, new { }, reply.FinishReason, cancellation.Token);
                if (input.TryGetProperty("stream_options", out var options) && options.ValueKind == JsonValueKind.Object && options.TryGetProperty("include_usage", out var include) && include.ValueKind == JsonValueKind.True && reply.Usage != null)
                    await SseAsync(context, new JsonObject { ["id"] = "chatcmpl-" + id, ["object"] = "chat.completion.chunk", ["created"] = created, ["model"] = model, ["choices"] = new JsonArray(), ["usage"] = reply.Usage.DeepClone() }, cancellation.Token);
                await context.Response.WriteAsync("data: [DONE]\n\n", cancellation.Token); await context.Response.Body.FlushAsync(cancellation.Token);
            }
            else
            {
                var message = new JsonObject { ["role"] = "assistant", ["content"] = reply.Text.Length == 0 && reply.Calls.Count > 0 ? null : JsonValue.Create(reply.Text), ["reasoning_content"] = reply.Reasoning };
                if (reply.Calls.Count > 0) message["tool_calls"] = new JsonArray(reply.Calls.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments } }).ToArray());
                var response = new JsonObject { ["id"] = "chatcmpl-" + id, ["object"] = "chat.completion", ["created"] = created, ["model"] = model, ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = reply.FinishReason }) };
                if (reply.Usage != null) response["usage"] = reply.Usage.DeepClone();
                await context.Response.WriteAsJsonAsync(response, cancellation.Token);
            }
            Interlocked.Increment(ref success); Volatile.Write(ref state, 0);
        }
        catch (Exception ex)
        {
            if (counted) { Interlocked.Increment(ref failed); Volatile.Write(ref state, ex is UpstreamException { Status: 401 } ? 3 : ex is OperationCanceledException ? 0 : 2); }
            if (context.RequestAborted.IsCancellationRequested || lifetime.IsCancellationRequested) return;
            int status = ex switch { UpstreamException upstream => upstream.Status, BadHttpRequestException bad => bad.StatusCode, JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException => 400, OperationCanceledException => 504, _ => 502 };
            string message = ex switch { UpstreamException or ArgumentException => ex.Message, OperationCanceledException => acquired ? "Buddy 上游请求超时。" : "Buddy 排队或读取请求超时。", JsonException or InvalidOperationException or KeyNotFoundException or FormatException => "请求字段或 JSON 格式无效。", BadHttpRequestException => "请求体无效或超过 1 MB。", _ => "Buddy 连接失败，请检查网络。" };
            if (counted) log?.Invoke(message);
            try { await ErrorAsync(context, status, message, context.RequestAborted); } catch (Exception failure) when (failure is IOException or OperationCanceledException) { }
        }
        finally { if (acquired) gate.Release(); }
    }
    private static async Task ChunkAsync(HttpContext context, string id, long created, string model, object delta, string? finish, CancellationToken cancellation) => await SseAsync(context, JsonSerializer.SerializeToNode(new { id = "chatcmpl-" + id, @object = "chat.completion.chunk", created, model, choices = new[] { new { index = 0, delta, finish_reason = finish } } })!.AsObject(), cancellation);
    private static async Task SseAsync(HttpContext context, JsonObject frame, CancellationToken cancellation, string? eventName = null)
    {
        await context.Response.WriteAsync((eventName == null ? "" : "event: " + eventName + "\n") + "data: " + frame.ToJsonString() + "\n\n", cancellation); await context.Response.Body.FlushAsync(cancellation);
    }
    private static async Task ResponsesStreamAsync(HttpContext context, JsonObject response, CancellationToken cancellation)
    {
        int sequence = 0;
        async Task Emit(string type, JsonObject body) { body["type"] = type; body["sequence_number"] = sequence++; await SseAsync(context, body, cancellation, type); }
        var initial = (JsonObject)response.DeepClone(); initial["status"] = "in_progress"; initial["output"] = new JsonArray(); initial["usage"] = null; initial["incomplete_details"] = null;
        await Emit("response.created", new JsonObject { ["response"] = initial.DeepClone() });
        await Emit("response.in_progress", new JsonObject { ["response"] = initial.DeepClone() });
        var output = response["output"]!.AsArray();
        for (int i = 0; i < output.Count; i++)
        {
            var item = output[i]!.AsObject(); string itemId = item["id"]!.ToString(), type = item["type"]!.ToString();
            var pending = (JsonObject)item.DeepClone(); if (type == "message") { pending["status"] = "in_progress"; pending["content"] = new JsonArray(); } if (type == "function_call") { pending["status"] = "in_progress"; pending["arguments"] = ""; } if (type == "reasoning") pending["summary"] = new JsonArray();
            await Emit("response.output_item.added", new JsonObject { ["output_index"] = i, ["item"] = pending });
            if (type == "message")
            {
                var part = item["content"]![0]!.AsObject();
                await Emit("response.content_part.added", new JsonObject { ["item_id"] = itemId, ["output_index"] = i, ["content_index"] = 0, ["part"] = new JsonObject { ["type"] = "output_text", ["text"] = "", ["annotations"] = new JsonArray(), ["logprobs"] = new JsonArray() } });
                await Emit("response.output_text.delta", new JsonObject { ["item_id"] = itemId, ["output_index"] = i, ["content_index"] = 0, ["delta"] = part["text"]!.DeepClone(), ["logprobs"] = new JsonArray() });
                await Emit("response.output_text.done", new JsonObject { ["item_id"] = itemId, ["output_index"] = i, ["content_index"] = 0, ["text"] = part["text"]!.DeepClone(), ["logprobs"] = new JsonArray() });
                await Emit("response.content_part.done", new JsonObject { ["item_id"] = itemId, ["output_index"] = i, ["content_index"] = 0, ["part"] = part.DeepClone() });
            }
            if (type == "function_call")
            {
                await Emit("response.function_call_arguments.delta", new JsonObject { ["item_id"] = itemId, ["output_index"] = i, ["delta"] = item["arguments"]!.DeepClone() });
                await Emit("response.function_call_arguments.done", new JsonObject { ["item_id"] = itemId, ["output_index"] = i, ["arguments"] = item["arguments"]!.DeepClone() });
            }
            if (type == "reasoning")
            {
                var part = item["summary"]![0]!.AsObject();
                await Emit("response.reasoning_summary_part.added", new JsonObject { ["item_id"] = itemId, ["output_index"] = i, ["summary_index"] = 0, ["part"] = new JsonObject { ["type"] = "summary_text", ["text"] = "" } });
                await Emit("response.reasoning_summary_text.delta", new JsonObject { ["item_id"] = itemId, ["output_index"] = i, ["summary_index"] = 0, ["delta"] = part["text"]!.DeepClone() });
                await Emit("response.reasoning_summary_text.done", new JsonObject { ["item_id"] = itemId, ["output_index"] = i, ["summary_index"] = 0, ["text"] = part["text"]!.DeepClone() });
                await Emit("response.reasoning_summary_part.done", new JsonObject { ["item_id"] = itemId, ["output_index"] = i, ["summary_index"] = 0, ["part"] = part.DeepClone() });
            }
            await Emit("response.output_item.done", new JsonObject { ["output_index"] = i, ["item"] = item.DeepClone() });
        }
        await Emit(response["status"]?.ToString() == "incomplete" ? "response.incomplete" : "response.completed", new JsonObject { ["response"] = response.DeepClone() });
    }
    private static async Task ErrorAsync(HttpContext context, int status, string message, CancellationToken cancellation)
    {
        var error = new { error = new { message, type = "buddy_error", code = status } };
        if (context.Response.HasStarted) { await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(error) + "\n\n", cancellation); await context.Response.Body.FlushAsync(cancellation); }
        else { context.Response.StatusCode = status; await context.Response.WriteAsJsonAsync(error, cancellation); }
    }
    public async ValueTask DisposeAsync()
    {
        ready = false; lifetime.Cancel();
        if (app != null) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); try { await app.StopAsync(timeout.Token); } finally { await app.DisposeAsync(); app = null; } }
        http.Dispose();
    }
}
