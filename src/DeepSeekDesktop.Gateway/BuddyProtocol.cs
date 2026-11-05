using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DeepSeekDesktop.Gateway;

public sealed record BuddyCall(string Id, string Name, string Arguments);
public sealed record BuddyReply(string Text, string Reasoning, IReadOnlyList<BuddyCall> Calls, string FinishReason, JsonObject? Usage);

public static class BuddyProtocol
{
    private static readonly string[] ChatFields = ["model", "messages", "stream", "stream_options", "tools", "tool_choice", "parallel_tool_calls", "reasoning_effort", "thinking", "max_tokens", "max_completion_tokens", "temperature", "top_p", "stop", "n", "response_format", "web_search_options"];
    private static readonly string[] ResponseFields = ["model", "input", "instructions", "stream", "tools", "tool_choice", "parallel_tool_calls", "reasoning", "max_output_tokens", "temperature", "top_p", "store", "include", "text", "previous_response_id", "truncation"];
    public static bool Streaming(JsonElement input) => input.TryGetProperty("stream", out var value) && value.ValueKind != JsonValueKind.Null ? value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new ArgumentException("stream 应为布尔值。") : false;
    private static void Fields(JsonObject input, string[] allowed)
    {
        foreach (var field in input)
            if (!allowed.Contains(field.Key, StringComparer.Ordinal)) throw new ArgumentException($"Buddy 适配暂不支持参数 {SafeName(field.Key)}。");
    }
    private static string SafeName(string value) => Regex.IsMatch(value, "^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant) ? value : "（无效字段名）";
    private static string Text(JsonNode? value, params string[] types)
    {
        if (value == null) return "";
        if (value is JsonValue primitive && primitive.TryGetValue<string>(out var text)) return text;
        if (value is not JsonArray blocks) throw new ArgumentException("消息内容应为文本或文本块数组。");
        var result = new StringBuilder();
        foreach (var block in blocks)
        {
            if (block is not JsonObject item || !types.Contains(item["type"]?.ToString(), StringComparer.Ordinal) || item["text"] is not JsonValue data || !data.TryGetValue<string>(out var part)) throw new ArgumentException("Buddy 当前仅适配文本内容，图片、音频与文件暂不支持。");
            result.Append(part);
        }
        return result.ToString();
    }
    private static bool Name(string value) => Regex.IsMatch(value, "^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);
    private static bool Identifier(string value) => value.Length is > 0 and <= 256 && value.All(c => char.IsAsciiLetterOrDigit(c) || "_-.".Contains(c));
    private static bool KnownEffort(string value) => value is "none" or "low" or "medium" or "high" or "xhigh" or "max";
    private static string? DefaultEffort(BuddyModel model, string[] supported)
    {
        string? preferred = model.DefaultReasoningEffort;
        if (preferred != null && KnownEffort(preferred) && (supported.Length == 0 || supported.Contains(preferred, StringComparer.Ordinal))) return preferred;
        if (supported.Contains("high", StringComparer.Ordinal)) return "high";
        return supported.FirstOrDefault();
    }
    internal static string Arguments(string value)
    {
        if (value.Length > 262144) throw new ArgumentException("工具参数过大。");
        using var json = JsonDocument.Parse(value);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("工具参数必须是 JSON 对象。");
        return value;
    }
    public static JsonObject Chat(JsonElement input, IReadOnlyList<BuddyModel> catalog)
    {
        if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException("请求应为 JSON 对象。");
        var request = JsonNode.Parse(input.GetRawText())!.AsObject(); Fields(request, ChatFields);
        string model = request["model"]?.GetValue<string>() ?? "";
        var info = catalog.FirstOrDefault(m => m.Id == model) ?? throw new ArgumentException("请使用当前账号 /v1/models 返回的模型名称。");
        if (request["n"] != null && request["n"]!.GetValue<int>() != 1) throw new ArgumentException("Buddy 每次仅支持一个回答。");
        request.Remove("n");
        if (request["messages"] is not JsonArray messages || messages.Count == 0 || messages.Count > 1000) throw new ArgumentException("messages 不能为空，且最多 1000 条。");
        var pending = new HashSet<string>(StringComparer.Ordinal); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in messages)
        {
            if (entry is not JsonObject message) throw new ArgumentException("消息格式无效。");
            foreach (var field in message)
                if (field.Key is not ("role" or "content" or "reasoning_content" or "tool_calls" or "tool_call_id" or "name")) throw new ArgumentException("消息包含不支持的字段。");
            string role = message["role"]?.GetValue<string>() ?? "";
            if (role == "tool")
            {
                string id = message["tool_call_id"]?.GetValue<string>() ?? "";
                if (!pending.Remove(id) || message["tool_calls"] != null || message["reasoning_content"] != null) throw new ArgumentException("tool_call_id 必须对应尚未回传的工具调用。");
                message["content"] = Text(message["content"], "text"); continue;
            }
            if (pending.Count > 0) throw new ArgumentException("请先回传上一轮全部工具结果。");
            if (role is not ("user" or "assistant" or "system" or "developer")) throw new ArgumentException("不支持的消息角色。");
            if (message["tool_call_id"] != null) throw new ArgumentException("仅 tool 消息可带 tool_call_id。");
            string text = Text(message["content"], "text");
            if (message["tool_calls"] is JsonArray calls)
            {
                if (role != "assistant" || calls.Count is < 1 or > 16) throw new ArgumentException("历史工具调用格式无效。");
                foreach (var call in calls)
                {
                    string id = call?["id"]?.GetValue<string>() ?? "", name = call?["function"]?["name"]?.GetValue<string>() ?? "";
                    if (call?["type"]?.ToString() != "function" || !Identifier(id) || !ids.Add(id) || !Name(name)) throw new ArgumentException("历史工具名称或 ID 无效。");
                    if (call?["function"]?["arguments"] is not JsonValue arguments || !arguments.TryGetValue<string>(out var raw)) throw new ArgumentException("历史工具调用缺少 arguments 字符串。");
                    Arguments(raw); pending.Add(id);
                }
                message["content"] = text.Length == 0 ? null : JsonValue.Create(text);
            }
            else
            {
                if (message["tool_calls"] != null || message["content"] == null) throw new ArgumentException("消息缺少文本内容。");
                message["content"] = text;
            }
            if (role == "assistant") message["reasoning_content"] = message["reasoning_content"]?.GetValue<string>() ?? "";
            else if (message["reasoning_content"] != null) throw new ArgumentException("仅 assistant 消息可带 reasoning_content。");
            if (role == "developer") message["role"] = "system";
        }
        if (pending.Count > 0) throw new ArgumentException("历史工具调用缺少执行结果。");
        // The upstream gateway requires a leading system role, including for a
        // one-message connection test. Preserve every caller instruction as-is.
        if (messages[0]?["role"]?.ToString() != "system") messages.Insert(0, new JsonObject { ["role"] = "system", ["content"] = "You are a helpful assistant." });
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (request["tools"] != null)
        {
            if (request["tools"] is not JsonArray tools || tools.Count > 64 || Encoding.UTF8.GetByteCount(tools.ToJsonString()) > 65536) throw new ArgumentException("tools 最多 64 个，定义合计最多 64 KiB。");
            foreach (var tool in tools)
            {
                if (tool is not JsonObject definition || definition["type"]?.ToString() != "function" || definition["function"] is not JsonObject function) throw new ArgumentException("仅支持 function 工具，由客户端执行。");
                string name = function["name"]?.GetValue<string>() ?? "";
                if (!Name(name) || !names.Add(name)) throw new ArgumentException("工具名无效或重复。");
                // Some clients omit parameters for argument-free tools; an explicit schema must still be an object.
                if (function["parameters"] == null) function["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
                if (function["parameters"] is not JsonObject schema || schema["type"]?.ToString() != "object") throw new ArgumentException("工具 parameters 必须声明 object 类型。");
            }
        }
        if (request["tool_choice"] is JsonValue choice)
        {
            string mode = choice.GetValue<string>();
            if (mode is not ("auto" or "none" or "required") || mode == "required" && names.Count == 0) throw new ArgumentException("tool_choice 无效或 tools 为空。");
        }
        else if (request["tool_choice"] is JsonObject selected)
        {
            if (selected["type"]?.ToString() != "function" || !names.Contains(selected["function"]?["name"]?.ToString() ?? "")) throw new ArgumentException("tool_choice 指定了未声明的工具。");
        }
        else if (request["tool_choice"] != null) throw new ArgumentException("tool_choice 格式无效。");
        if (request["parallel_tool_calls"] != null) request["parallel_tool_calls"]!.GetValue<bool>();
        foreach (string key in new[] { "max_tokens", "max_completion_tokens" })
            if (request[key] != null && (request[key]!.GetValue<int>() is < 1 or > 131072 || info.MaxOutputTokens is int maximum && request[key]!.GetValue<int>() > maximum)) throw new ArgumentException("输出 token 上限超过当前模型声明范围。");
        if (request["max_tokens"] != null && request["max_completion_tokens"] != null) throw new ArgumentException("max_tokens 与 max_completion_tokens 请只指定一个。");
        if (request["max_completion_tokens"] != null) { request["max_tokens"] = request["max_completion_tokens"]!.DeepClone(); request.Remove("max_completion_tokens"); }
        if (request["max_tokens"] == null && info.MaxOutputTokens is int declaredMaximum) request["max_tokens"] = declaredMaximum;
        foreach (string key in new[] { "temperature", "top_p" })
            if (request[key] != null && (!double.IsFinite(request[key]!.GetValue<double>()) || request[key]!.GetValue<double>() < 0 || request[key]!.GetValue<double>() > (key == "temperature" ? 2 : 1))) throw new ArgumentException($"{key} 超出允许范围。");
        if (request["stop"] != null && !(request["stop"] is JsonValue stop && stop.TryGetValue<string>(out _) || request["stop"] is JsonArray stops && stops.Count <= 4 && stops.All(s => s is JsonValue v && v.TryGetValue<string>(out _)))) throw new ArgumentException("stop 应为字符串或最多 4 个字符串的数组。");
        if (request["response_format"] != null && request["response_format"]?["type"]?.ToString() != "text") throw new ArgumentException("Buddy 适配暂不支持结构化输出约束。");
        request.Remove("response_format");
        if (request["web_search_options"] != null)
        {
            if (request["web_search_options"] is not JsonObject search || search.Count != 1 || search["search_context_size"]?.ToString() != "none") throw new ArgumentException("Buddy 内置搜索暂不支持，请由客户端配置搜索工具。");
            request.Remove("web_search_options");
        }
        if (request["stream_options"] is not null)
        {
            if (request["stream_options"] is not JsonObject options || options.Any(p => p.Key != "include_usage")) throw new ArgumentException("stream_options 仅支持 include_usage。");
            if (options["include_usage"] != null) options["include_usage"]!.GetValue<bool>();
        }
        request.Remove("stream_options");
        bool deepseek = model.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase);
        string[] supported = (info.ReasoningEfforts ?? []).Where(KnownEffort).Distinct(StringComparer.Ordinal).ToArray();
        string? effort = request["reasoning_effort"]?.GetValue<string>();
        if (effort != null && !KnownEffort(effort)) throw new ArgumentException("当前模型不支持指定的思考档位。");
        if (request["thinking"] != null)
        {
            if (request["thinking"] is not JsonObject thinking || thinking.Count != 1 || thinking["type"]?.ToString() is not ("enabled" or "disabled")) throw new ArgumentException("thinking 仅支持 type=enabled 或 disabled。");
            if (!deepseek) throw new ArgumentException("当前适配仅为 DeepSeek 模型提供显式思考开关。");
            string type = thinking["type"]!.ToString();
            if (effort != null && (type == "disabled") != (effort == "none")) throw new ArgumentException("thinking 与 reasoning_effort 冲突。");
            if (effort == null)
            {
                if (type == "disabled") effort = "none";
                else
                {
                    string[] enabledEfforts = supported.Where(value => value != "none").ToArray();
                    effort = DefaultEffort(info, enabledEfforts);
                    if (effort == "none") effort = null;
                    if (effort == null) effort = supported.Length == 0 ? "high" : throw new ArgumentException("当前模型未声明可启用的思考档位。");
                }
            }
        }
        if (effort == null && deepseek) effort = DefaultEffort(info, supported);
        if (effort is not null)
        {
            if (effort != "none" && supported.Length > 0 && !supported.Contains(effort, StringComparer.Ordinal)) throw new ArgumentException("当前模型不支持指定的思考档位。");
            if (deepseek) request["thinking"] = new JsonObject { ["type"] = effort == "none" ? "disabled" : "enabled" };
            else if (request["thinking"] != null || effort == "none") throw new ArgumentException("当前适配仅为 DeepSeek 模型提供显式思考开关。");
            if (effort == "none") request.Remove("reasoning_effort"); else request["reasoning_effort"] = effort;
        }
        request["stream"] = true;
        return request;
    }
    public static JsonObject Responses(JsonElement input, IReadOnlyList<BuddyModel> catalog)
    {
        if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException("请求应为 JSON 对象。");
        var source = JsonNode.Parse(input.GetRawText())!.AsObject(); Fields(source, ResponseFields);
        if (source["previous_response_id"] != null) throw new ArgumentException("Buddy 使用无存储模式，请回传完整 input，不使用 previous_response_id。");
        if (source["store"] != null && source["store"]!.GetValue<bool>()) throw new ArgumentException("Buddy 不保存 Responses 会话，请使用 store: false。");
        // Encrypted reasoning is never produced here, so asking for it is harmless; other extras still fail.
        if (source["include"] != null && (source["include"] is not JsonArray include || include.Any(item => item?.ToString() != "reasoning.encrypted_content"))) throw new ArgumentException("Buddy 不提供加密思考等额外 include 内容。");
        if (source["truncation"] != null && source["truncation"]?.ToString() != "disabled") throw new ArgumentException("Buddy 暂不支持自动截断历史。");
        if (source["text"] != null && (source["text"] is not JsonObject text || text.Count != 1 || text["format"] is not JsonObject format || format.Count != 1 || format["type"]?.ToString() != "text")) throw new ArgumentException("Responses 暂仅支持普通文本输出。");
        var request = new JsonObject { ["model"] = source["model"]?.DeepClone(), ["messages"] = new JsonArray() };
        var messages = request["messages"]!.AsArray();
        if (source["instructions"] != null) messages.Add(new JsonObject { ["role"] = "system", ["content"] = source["instructions"]!.GetValue<string>() });
        JsonArray turns = source["input"] is JsonValue value && value.TryGetValue<string>(out var prompt) ? new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }) : source["input"] as JsonArray ?? throw new ArgumentException("Responses input 应为文本或消息数组。");
        if (turns.Count == 0) throw new ArgumentException("Responses input 不能为空。");
        string reasoning = "";
        JsonObject? assistantRound = null;
        JsonObject AssistantRound()
        {
            if (assistantRound == null)
            {
                assistantRound = new JsonObject { ["role"] = "assistant", ["content"] = null, ["reasoning_content"] = reasoning };
                reasoning = ""; messages.Add(assistantRound);
            }
            return assistantRound;
        }
        void EndAssistantRound()
        {
            if (reasoning.Length > 0) throw new ArgumentException("思考上下文后缺少对应的助手消息或工具调用。");
            assistantRound = null;
        }
        foreach (var node in turns)
        {
            if (node is not JsonObject turn) throw new ArgumentException("Responses input 项无效。");
            string? type = turn["type"]?.ToString();
            if (type == "reasoning")
            {
                // Replayed history may carry another provider's opaque reasoning; it cannot be
                // decrypted here, so drop it and keep only the readable summary.
                string part = Text(turn["summary"], "summary_text");
                if (assistantRound == null) reasoning += part;
                else assistantRound["reasoning_content"] = (assistantRound["reasoning_content"]?.GetValue<string>() ?? "") + part;
                continue;
            }
            if (type == "function_call")
            {
                var call = new JsonObject { ["id"] = turn["call_id"]?.DeepClone(), ["type"] = "function", ["function"] = new JsonObject { ["name"] = turn["name"]?.DeepClone(), ["arguments"] = turn["arguments"]?.DeepClone() } };
                var assistant = AssistantRound();
                if (assistant["tool_calls"] is not JsonArray calls) { calls = new JsonArray(); assistant["tool_calls"] = calls; }
                calls.Add(call); continue;
            }
            if (type == "function_call_output")
            {
                EndAssistantRound();
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = turn["call_id"]?.DeepClone(), ["content"] = Text(turn["output"], "input_text", "text") }); continue;
            }
            if (type is not (null or "message")) throw new ArgumentException("Responses input 包含暂不支持的项目类型。");
            string role = turn["role"]?.GetValue<string>() ?? "";
            string content = Text(turn["content"], "input_text", "output_text", "text");
            if (role == "assistant")
            {
                var assistant = AssistantRound(); assistant["content"] = (assistant["content"]?.GetValue<string>() ?? "") + content;
            }
            else { EndAssistantRound(); messages.Add(new JsonObject { ["role"] = role, ["content"] = content }); }
        }
        if (reasoning.Length > 0) throw new ArgumentException("思考上下文后缺少对应的助手消息或工具调用。");
        if (source["tools"] != null)
        {
            if (source["tools"] is not JsonArray tools) throw new ArgumentException("tools 应为数组。");
            var converted = new JsonArray();
            foreach (var tool in tools)
            {
                if (tool is not JsonObject definition || definition["type"]?.ToString() != "function") throw new ArgumentException("Responses 仅支持客户端 function 工具。");
                var function = (JsonObject)definition.DeepClone(); function.Remove("type"); converted.Add(new JsonObject { ["type"] = "function", ["function"] = function });
            }
            request["tools"] = converted;
        }
        if (source["tool_choice"] is JsonObject selected)
        {
            if (selected["type"]?.ToString() != "function") throw new ArgumentException("Responses tool_choice 仅支持 function。");
            request["tool_choice"] = new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = selected["name"]?.DeepClone() } };
        }
        else if (source["tool_choice"] != null) request["tool_choice"] = source["tool_choice"]!.DeepClone();
        foreach (string field in new[] { "parallel_tool_calls", "temperature", "top_p" }) if (source[field] != null) request[field] = source[field]!.DeepClone();
        if (source["max_output_tokens"] != null) request["max_tokens"] = source["max_output_tokens"]!.DeepClone();
        if (source["reasoning"] != null)
        {
            if (source["reasoning"] is not JsonObject options || options.Any(p => p.Key is not ("effort" or "summary")) || options["summary"]?.ToString() is not (null or "auto" or "concise" or "detailed")) throw new ArgumentException("Responses reasoning 参数无效。");
            if (options["effort"] != null) request["reasoning_effort"] = options["effort"]!.DeepClone();
        }
        return Chat(JsonSerializer.SerializeToElement(request), catalog);
    }
    public static JsonObject Response(BuddyReply reply, string id, string model, long created)
    {
        var output = new JsonArray();
        if (reply.Reasoning.Length > 0) output.Add(new JsonObject { ["id"] = "rs_" + id, ["type"] = "reasoning", ["summary"] = new JsonArray(new JsonObject { ["type"] = "summary_text", ["text"] = reply.Reasoning }) });
        if (reply.Text.Length > 0) output.Add(new JsonObject { ["id"] = "msg_" + id, ["type"] = "message", ["role"] = "assistant", ["status"] = reply.FinishReason == "length" ? "incomplete" : "completed", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = reply.Text, ["annotations"] = new JsonArray(), ["logprobs"] = new JsonArray() }) });
        foreach (var call in reply.Calls) output.Add(new JsonObject { ["id"] = "fc_" + call.Id, ["type"] = "function_call", ["status"] = "completed", ["call_id"] = call.Id, ["name"] = call.Name, ["arguments"] = call.Arguments });
        var response = new JsonObject { ["id"] = "resp_" + id, ["object"] = "response", ["created_at"] = created, ["status"] = reply.FinishReason == "length" ? "incomplete" : "completed", ["error"] = null, ["incomplete_details"] = reply.FinishReason == "length" ? new JsonObject { ["reason"] = "max_output_tokens" } : null, ["model"] = model, ["output"] = output, ["parallel_tool_calls"] = true, ["store"] = false };
        if (reply.Usage != null) response["usage"] = new JsonObject { ["input_tokens"] = reply.Usage["prompt_tokens"]?.DeepClone(), ["output_tokens"] = reply.Usage["completion_tokens"]?.DeepClone(), ["total_tokens"] = reply.Usage["total_tokens"]?.DeepClone(), ["input_tokens_details"] = reply.Usage["prompt_tokens_details"]?.DeepClone(), ["output_tokens_details"] = reply.Usage["completion_tokens_details"]?.DeepClone() };
        return response;
    }
}

internal sealed class BuddyResult
{
    private sealed class Call { public string Id = "", Name = ""; public readonly StringBuilder Arguments = new(); }
    private readonly StringBuilder text = new(), reasoning = new();
    private readonly SortedDictionary<int, Call> calls = new();
    private JsonObject? usage;
    private string? finish;
    public (string Text, string Reasoning) Add(JsonObject frame)
    {
        try
        {
            if (frame["usage"] is JsonObject counters) usage = (JsonObject)counters.DeepClone();
            if (frame["choices"] is not JsonArray choices || choices.Count > 1) throw new JsonException();
            string deltaText = "", deltaReasoning = "";
            foreach (var choice in choices)
            {
                if (choice is not JsonObject item || item["index"]?.GetValue<int>() != 0 || item["delta"] is not JsonObject delta) throw new JsonException();
                deltaText = delta["content"]?.GetValue<string>() ?? ""; deltaReasoning = delta["reasoning_content"]?.GetValue<string>() ?? "";
                if (finish != null && (deltaText.Length > 0 || deltaReasoning.Length > 0 || delta["tool_calls"] != null)) throw new JsonException();
                text.Append(deltaText); reasoning.Append(deltaReasoning);
                if (text.Length + reasoning.Length > 2 * 1024 * 1024) throw new JsonException();
                if (delta["tool_calls"] is JsonArray list)
                    foreach (var node in list)
                    {
                        if (node is not JsonObject call || call["index"] is not JsonValue index || !index.TryGetValue<int>(out int key) || key is < 0 or >= 16) throw new JsonException();
                        if (!calls.TryGetValue(key, out var state)) { state = new(); calls.Add(key, state); }
                        if (call["type"]?.ToString() is not (null or "function")) throw new JsonException();
                        string id = call["id"]?.GetValue<string>() ?? "", name = call["function"]?["name"]?.GetValue<string>() ?? "";
                        if (id.Length > 0) { if (state.Id.Length > 0 && state.Id != id) throw new JsonException(); state.Id = id; }
                        if (name.Length > 0) { if (state.Name.Length > 0 && state.Name != name) throw new JsonException(); state.Name = name; }
                        state.Arguments.Append(call["function"]?["arguments"]?.GetValue<string>() ?? "");
                        if (state.Id.Length > 256 || state.Name.Length > 64 || state.Arguments.Length > 262144) throw new JsonException();
                    }
                string? reason = item["finish_reason"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(reason)) { if (finish != null) throw new JsonException(); finish = reason; }
            }
            return (deltaText, deltaReasoning);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException) { throw new UpstreamException(502, "Buddy 返回了无效的对话数据帧。"); }
    }
    public BuddyReply Complete(JsonObject request)
    {
        if (finish is not ("stop" or "tool_calls" or "length")) throw new UpstreamException(502, "Buddy 对话未正常完成，未下发工具调用。");
        var allowed = new HashSet<string>((request["tools"] as JsonArray ?? []).Select(t => t?["function"]?["name"]?.ToString() ?? ""), StringComparer.Ordinal);
        var result = new List<BuddyCall>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (index, call) in calls)
        {
            if (index != result.Count || !Regex.IsMatch(call.Id, "^[A-Za-z0-9_.-]{1,256}$", RegexOptions.CultureInvariant) || !ids.Add(call.Id) || !allowed.Contains(call.Name)) throw new UpstreamException(502, "Buddy 返回了无效或未声明的工具调用。");
            string arguments;
            try { arguments = BuddyProtocol.Arguments(call.Arguments.ToString()); }
            catch (Exception ex) when (ex is ArgumentException or JsonException) { throw new UpstreamException(502, "Buddy 工具参数不完整或无效，未发送给客户端执行。"); }
            result.Add(new(call.Id, call.Name, arguments));
        }
        if ((result.Count > 0) != (finish == "tool_calls") || request["parallel_tool_calls"]?.ToString() == "false" && result.Count > 1) throw new UpstreamException(502, "Buddy 工具调用数量或结束标记无效。");
        if (request["tool_choice"]?.ToString() == "none" && result.Count > 0 || request["tool_choice"]?.ToString() == "required" && result.Count == 0 || request["tool_choice"] is JsonObject choice && (result.Count == 0 || result.Any(c => c.Name != choice["function"]?["name"]?.ToString()))) throw new UpstreamException(502, "Buddy 返回的调用未遵循 tool_choice。");
        if (text.Length == 0 && result.Count == 0) throw new UpstreamException(502, "Buddy 未返回正文或工具调用。");
        return new(text.ToString(), reasoning.ToString(), result, finish, usage);
    }
}
