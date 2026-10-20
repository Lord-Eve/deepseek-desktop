using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DeepSeekDesktop.Core;
using DeepSeekDesktop.Gateway;

internal static class ToolBridgeChecks
{
    private const string ToolName = "mcp__exa__web_search_exa";
    private static readonly JsonElement Definition = JsonSerializer.Deserialize<JsonElement>("""{"type":"function","function":{"name":"mcp__exa__web_search_exa","description":"搜索网页并返回内容","parameters":{"type":"object","properties":{"query":{"type":"string","minLength":1},"numResults":{"type":"integer","minimum":1,"maximum":10},"type":{"enum":["auto","fast"]}},"required":["query"],"additionalProperties":false}}}""");
    private static readonly JsonElement Fetch = JsonSerializer.Deserialize<JsonElement>("""{"type":"function","function":{"name":"get_contents_exa","parameters":{"type":"object","properties":{"urls":{"type":"array","items":{"type":"string"},"minItems":1}},"required":["urls"]}}}""");
    private static object User => new { role = "user", content = "请用 Exa 搜索鲸鱼资料，并根据搜索结果回答。" };
    private static string Envelope(string args = "{\"query\":\"鲸鱼资料\",\"numResults\":3}", string name = ToolName) => "[function_calls]\n[call:" + name + "]" + args + "[/call]\n[/function_calls]";
    private static JsonElement Request(object? choice = null, bool stream = false, object[]? messages = null, bool parallel = true, JsonElement[]? tools = null) => JsonSerializer.SerializeToElement(new
    {
        model = "qingyan-default", messages = messages ?? [User], tools = tools ?? [Definition], tool_choice = choice ?? "auto", parallel_tool_calls = parallel, stream
    });

    public static async Task RunAsync(Action<bool, string> check)
    {
        void Reject(Action action, string message, bool upstream = true)
        {
            bool rejected = false;
            try { action(); }
            catch (UpstreamException ex) when (upstream && ex.Status == 502) { rejected = true; }
            catch (ArgumentException) when (!upstream) { rejected = true; }
            check(rejected, message);
        }
        var plan = QingyanTools.Prepare(Request());
        check(plan.Enabled && plan.Prompt.Contains(ToolName) && plan.Prompt.Contains("搜索网页并返回内容") && plan.Prompt.Contains("required"), "The prompt must include the actual MCP name, description and parameter schema.");
        var calls = plan.Parse(Envelope());
        using (var args = JsonDocument.Parse(calls.Single().Arguments)) check(calls[0].Name == ToolName && args.RootElement.GetProperty("query").GetString() == "鲸鱼资料", "A valid call must retain its declared name and JSON arguments.");
        var literal = plan.Parse(Envelope(JsonSerializer.Serialize(new { query = "字符 } 和 [/call] 必须原样保留" })));
        check(literal.Count == 1 && literal[0].Arguments.Contains("/call"), "Braces and closing markers inside JSON strings must not truncate arguments.");
        check(plan.Parse("无需搜索，这是一条普通回答。").Count == 0, "Auto mode must permit ordinary final answers.");
        check(QingyanTools.Prepare(Request("none")).Parse(Envelope()).Count == 0, "tool_choice none must never convert text into executable tool events.");
        Reject(() => QingyanTools.Prepare(Request("required")).Parse("我准备搜索。"), "Required mode must not pass off a textual plan as a tool call.");
        Reject(() => plan.Parse(Envelope("{}")), "Missing required MCP parameters must be rejected.");
        Reject(() => plan.Parse(Envelope("{\"query\":2}")), "Incorrect parameter types must be rejected.");
        Reject(() => plan.Parse(Envelope("{\"query\":\"q\",\"numResults\":0}")), "Parameter bounds must be enforced.");
        Reject(() => plan.Parse(Envelope("{\"query\":\"q\",\"type\":\"invented\"}")), "Enum values must match the tool definition.");
        Reject(() => plan.Parse(Envelope("{\"query\":\"q\",\"extra\":true}")), "additionalProperties false must not silently accept invented parameters.");
        Reject(() => plan.Parse(Envelope(name: "invented_search")), "Undeclared model-generated tools must not be returned for execution.");
        Reject(() => plan.Parse("[function_calls][call:" + ToolName + "]{\"query\":\"q\"}"), "Truncated tool envelopes must never execute.");
        Reject(() => plan.Parse(Envelope("{bad}")), "Malformed argument JSON must not be repaired into an empty call.");
        Reject(() => plan.Parse("这是例子：" + Envelope()), "A quoted or narrated envelope must not be treated as an executable call.");
        string pair = "[function_calls][call:" + ToolName + "]{\"query\":\"q\"}[/call][call:get_contents_exa]{\"urls\":[\"https://example.test\"]}[/call][/function_calls]";
        var parallel = QingyanTools.Prepare(Request(tools: [Definition, Fetch]));
        var pairCalls = parallel.Parse(pair);
        check(pairCalls.Count == 2 && pairCalls[0].Id != pairCalls[1].Id, "Multiple calls must have separate stable IDs.");
        Reject(() => QingyanTools.Prepare(Request(parallel: false, tools: [Definition, Fetch])).Parse(pair), "parallel_tool_calls false must limit the model to one call.");
        string duplicate = "[function_calls][call:" + ToolName + "]{\"query\":\"q\"}[/call][call:" + ToolName + "]{\"query\":\"q\"}[/call][/function_calls]";
        check(plan.Parse(duplicate).Count == 1, "Repeated identical model output must not cause duplicate tool execution.");
        object forced = new { type = "function", function = new { name = ToolName } };
        Reject(() => QingyanTools.Prepare(Request(forced, tools: [Definition, Fetch])).Parse(Envelope("{\"urls\":[\"https://example.test\"]}", "get_contents_exa")), "Named tool_choice must reject other declared tools too.");
        check(QingyanTools.Prepare(Request(forced)).Parse(Envelope()).Count == 1, "Named tool_choice must accept its requested tool.");
        Reject(() => QingyanTools.Prepare(Request("required", tools: [])), "Required choice needs a tool definition.", false);
        Reject(() => QingyanTools.Prepare(Request(tools: [Definition, Definition])), "Duplicate tool definitions must fail before sending an upstream request.", false);
        Reject(() => QingyanTools.Prepare(Request(messages: [new { role = "tool", tool_call_id = "missing", content = "result" }])), "Orphaned tool results must fail before upstream calls.", false);
        var assistant = new { role = "assistant", content = (string?)null, tool_calls = new[] { new { id = "call_fixture", type = "function", function = new { name = ToolName, arguments = "{\"query\":\"鲸鱼资料\"}" } } } };
        Reject(() => QingyanTools.Prepare(Request(messages: [User, assistant])), "History must not silently discard an unresolved tool call.", false);
        var history = QingyanTools.Prepare(Request(messages: [User, assistant, new { role = "tool", tool_call_id = "call_fixture", content = new[] { new { type = "text", text = "Exa 模拟结果：鲸鱼是哺乳动物。" } } }]));
        check(history.Prompt.Contains("[TOOL_RESULT for call_fixture] (" + ToolName + ")") && history.Prompt.Contains("鲸鱼是哺乳动物") && history.Prompt.Contains("[call:" + ToolName + "]"), "MCP history must preserve call IDs, tool names, arguments and text-block results.");

        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var settings = new Settings { Provider = Providers.Qingyan, Port = port, HourlyQuota = 30, Accounts = [new Account { Provider = Providers.Qingyan, RefreshToken = "fixture-tools-refresh-0123456789" }] };
        var upstream = new ToolUpstream { Reply = Envelope() };
        await using var gateway = new QingyanGateway(settings, handler: upstream);
        await gateway.StartAsync();
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(settings.BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        async Task<HttpResponseMessage> Send(JsonElement request) => await http.PostAsync("chat/completions", new StringContent(request.GetRawText(), Encoding.UTF8, "application/json"));
        JsonElement firstMessage;
        using (var response = await Send(Request()))
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var choice = json.RootElement.GetProperty("choices")[0]; firstMessage = choice.GetProperty("message").Clone();
            check(response.IsSuccessStatusCode && choice.GetProperty("finish_reason").GetString() == "tool_calls" && firstMessage.GetProperty("content").ValueKind == JsonValueKind.Null, "Non-streaming tool requests must return OpenAI tool_calls with null content and the correct finish reason.");
        }
        var function = firstMessage.GetProperty("tool_calls")[0];
        using var arguments = JsonDocument.Parse(function.GetProperty("function").GetProperty("arguments").GetString()!);
        check(function.GetProperty("function").GetProperty("name").GetString() == ToolName && arguments.RootElement.GetProperty("numResults").GetInt32() == 3 && upstream.ToolMode && !upstream.Search, "MCP arguments must reach the client; tool requests must use ordinary web mode without built-in search substitution.");
        // The test client executes a simulated Exa tool. The gateway never connects to MCP or Exa.
        int executions = 0;
        string ExecuteExa() { executions++; return "模拟 Exa 结果：鲸鱼是哺乳动物。来源 https://fixture.example/exa-result"; }
        object result = new { role = "tool", tool_call_id = function.GetProperty("id").GetString(), content = ExecuteExa() };
        upstream.Reply = "根据 Exa 搜索结果，鲸鱼是哺乳动物。";
        using (var response = await Send(Request(messages: [User, firstMessage, result])))
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var choice = json.RootElement.GetProperty("choices")[0];
            check(response.IsSuccessStatusCode && choice.GetProperty("finish_reason").GetString() == "stop" && choice.GetProperty("message").GetProperty("content").GetString()!.Contains("哺乳动物") && upstream.LastPrompt.Contains("https://fixture.example/exa-result") && executions == 1, "The complete mock Exa loop must execute once, return results with the matching ID and produce a final answer.");
        }
        upstream.Reply = Envelope();
        var (streamCalls, reason) = await StreamCalls(await Send(Request(stream: true)), check);
        check(streamCalls.Count == 1 && reason == "tool_calls" && streamCalls[0].GetProperty("index").GetInt32() == 0 && streamCalls[0].GetProperty("function").GetProperty("arguments").GetString()!.Contains("numResults"), "SSE must expose indexed tool_calls with complete arguments and finish_reason tool_calls.");
        var streamingCall = streamCalls[0];
        var streamedAssistant = new { role = "assistant", content = (string?)null, tool_calls = new[] { new { id = streamingCall.GetProperty("id").GetString(), type = "function", function = streamingCall.GetProperty("function") } } };
        upstream.Reply = "已根据客户端返回的搜索结果完成回答。";
        var (_, finalReason) = await StreamCalls(await Send(Request(stream: true, messages: [User, streamedAssistant, new { role = "tool", tool_call_id = streamingCall.GetProperty("id").GetString(), content = "模拟 Exa 搜索结果" }])), check);
        check(finalReason == "stop" && upstream.LastPrompt.Contains("模拟 Exa 搜索结果"), "Streaming tool results must also round-trip into a final answer.");
        upstream.Reply = "我准备搜索，但没有调用。";
        using (var response = await Send(Request("required"))) check((int)response.StatusCode == 502, "A model that ignores required choice must return a protocol error, not fake success.");
        upstream.Reply = "无需工具的普通回答。";
        using (var response = await Send(Request("none"))) check(response.IsSuccessStatusCode && !upstream.ToolMode && !upstream.LastPrompt.Contains("工具调用协议"), "tool_choice none must preserve ordinary text mode without tool instructions.");
        upstream.Reply = Envelope("{}");
        using (var response = await Send(Request(stream: true)))
        {
            string body = await response.Content.ReadAsStringAsync();
            check((int)response.StatusCode == 502 && !body.Contains("\"tool_calls\"") && !body.Contains("[DONE]"), "Invalid streaming arguments must fail before any executable tool event is emitted.");
        }
        upstream.Reply = Envelope(); upstream.Truncated = true;
        using (var response = await Send(Request(stream: true))) check((int)response.StatusCode == 502, "An upstream stream missing its completion marker must not execute even an apparently complete tool envelope.");
    }

    private static async Task<(List<JsonElement> Calls, string Finish)> StreamCalls(HttpResponseMessage response, Action<bool, string> check)
    {
        using (response)
        {
            string body = await response.Content.ReadAsStringAsync();
            check(response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "text/event-stream" && body.EndsWith("data: [DONE]\n\n", StringComparison.Ordinal) && !body.Contains("[function_calls]"), "Validated tool-aware SSE must use standard OpenAI events and conceal the internal envelope.");
            var calls = new List<JsonElement>(); string finish = "";
            foreach (string line in body.Split('\n').Where(l => l.StartsWith("data: {", StringComparison.Ordinal)))
            {
                using var frame = JsonDocument.Parse(line[6..]);
                var choice = frame.RootElement.GetProperty("choices")[0];
                if (choice.GetProperty("delta").TryGetProperty("tool_calls", out var toolCalls)) calls.AddRange(toolCalls.EnumerateArray().Select(c => c.Clone()));
                if (choice.GetProperty("finish_reason").ValueKind == JsonValueKind.String) finish = choice.GetProperty("finish_reason").GetString()!;
            }
            return (calls, finish);
        }
    }

    private sealed class ToolUpstream : HttpMessageHandler
    {
        public string Reply = "", LastPrompt = "";
        public bool ToolMode, Search, Truncated;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            if (request.RequestUri?.Host != "chatglm.cn") throw new Exception("Unexpected upstream host.");
            if (request.RequestUri.AbsolutePath.EndsWith("/refresh")) return new(HttpStatusCode.OK) { Content = new StringContent("{\"result\":{\"access_token\":\"fixture-tools-access\"}}", Encoding.UTF8, "application/json") };
            if (request.RequestUri.AbsolutePath.EndsWith("/delete")) return new(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
            LastPrompt = json.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString()!;
            var meta = json.RootElement.GetProperty("meta_data");
            ToolMode = meta.TryGetProperty("chat_mode", out var mode) && mode.GetString() == "zero";
            Search = meta.GetProperty("is_networking").GetBoolean();
            string Frame(string text, string state) => "data: " + JsonSerializer.Serialize(new { conversation_id = "fixture-tools-conversation", status = state, parts = new[] { new { content = new[] { new { type = "text", text } } } } }) + "\n\n";
            string output = Frame(Reply[..Math.Min(9, Reply.Length)], "processing") + Frame(Reply[..Math.Min(33, Reply.Length)], "processing") + (Truncated ? "" : Frame(Reply, "finish"));
            return new(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedStream(Encoding.UTF8.GetBytes(output))) { Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") } } };
        }
    }
}
