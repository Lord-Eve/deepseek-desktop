using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DeepSeekDesktop.Gateway;

public sealed record GrokCall(string Id, string Name, string Arguments);
public sealed record GrokCompletion(string Text, string Reasoning, IReadOnlyList<GrokCall> Calls, JsonObject Response);

public sealed class GrokReplayCache
{
    private sealed record Entry(JsonArray Output, DateTimeOffset Expires, int Bytes);
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private int size;
    private static string Key(JsonObject request, IReadOnlyList<GrokCall> calls) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request["model"] + "\n" + request["instructions"] + "\n" + request["input"]?.ToJsonString() + "\n" + JsonSerializer.Serialize(calls.Select(c => new { c.Id, c.Name, Arguments = GrokProtocol.Arguments(c.Arguments) })))));
    public JsonArray Get(JsonObject request, IReadOnlyList<GrokCall> calls)
    {
        string key = Key(request, calls);
        if (!entries.TryGetValue(key, out var entry) || entry.Expires <= DateTimeOffset.UtcNow) throw new ArgumentException("Grok 工具上下文已过期、服务已重启或历史被改写，请在客户端新建会话后重试。");
        return (JsonArray)entry.Output.DeepClone();
    }
    public void Save(JsonObject request, GrokCompletion completion)
    {
        if (completion.Calls.Count == 0 || completion.Response["output"] is not JsonArray output) return;
        int bytes = Encoding.UTF8.GetByteCount(output.ToJsonString());
        if (bytes > 4 * 1024 * 1024) throw new UpstreamException(502, "Grok 工具上下文过大，请新建会话后重试。");
        foreach (string old in entries.Where(p => p.Value.Expires <= DateTimeOffset.UtcNow).Select(p => p.Key).ToArray()) { size -= entries[old].Bytes; entries.Remove(old); }
        while (entries.Count >= 128 || size + bytes > 16 * 1024 * 1024) { string old = entries.Keys.First(); size -= entries[old].Bytes; entries.Remove(old); }
        string key = Key(request, completion.Calls);
        if (entries.Remove(key, out var previous)) size -= previous.Bytes;
        entries[key] = new((JsonArray)output.DeepClone(), DateTimeOffset.UtcNow.AddMinutes(30), bytes); size += bytes;
    }
    public void Clear() { entries.Clear(); size = 0; }
}

public static class GrokProtocol
{
    // Preset catalog from Cherry Studio v2.1.6, not a guarantee of an account's entitlement.
    public static readonly string[] Models = ["grok-build", "grok-4.7", "grok-4.5", "grok-4.20-0309-reasoning", "grok-composer-2.5-fast"];
    public static string Model(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.String) throw new ArgumentException("请填写 Grok 模型名。");
        string value = model.GetString()!;
        if (value.StartsWith("grok-cli/", StringComparison.Ordinal)) value = value[9..];
        if (!Models.Contains(value, StringComparer.Ordinal)) throw new ArgumentException("当前 Grok 预设目录不包含该模型，请使用本地 /v1/models 返回的名称。");
        return value;
    }
    public static bool Streaming(JsonElement input) => input.TryGetProperty("stream", out var stream) && stream.ValueKind != JsonValueKind.Null ? stream.ValueKind is JsonValueKind.True or JsonValueKind.False ? stream.GetBoolean() : throw new ArgumentException("stream 应为布尔值。") : false;
    internal static string Arguments(string value)
    {
        using var json = JsonDocument.Parse(value);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("工具参数必须为 JSON 对象。");
        return JsonSerializer.Serialize(json.RootElement);
    }
    private static string Content(JsonElement message, bool optional = false)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind == JsonValueKind.Null) return optional ? "" : throw new ArgumentException("消息缺少文本内容。");
        if (content.ValueKind == JsonValueKind.String) return content.GetString()!;
        if (content.ValueKind != JsonValueKind.Array) throw new ArgumentException("Chat Completions 暂仅支持文本内容。");
        var text = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("type", out var type) || type.ToString() != "text" || !block.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String) throw new ArgumentException("Grok Chat Completions 适配暂仅支持文本，图片、音频和文件请勿传入。");
            text.Append(value.GetString());
        }
        return text.ToString();
    }
    private static bool Name(string value) => Regex.IsMatch(value, "^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);
    public static JsonObject Chat(JsonElement input, GrokReplayCache replay)
    {
        var request = new JsonObject { ["model"] = Model(input), ["input"] = new JsonArray() };
        if (input.TryGetProperty("functions", out _) || input.TryGetProperty("function_call", out _)) throw new ArgumentException("请使用 tools/tool_choice 工具协议。");
        if (input.TryGetProperty("n", out var count) && (!count.TryGetInt32(out int number) || number != 1)) throw new ArgumentException("Grok 每次仅支持一个回答。");
        if (!input.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() == 0) throw new ArgumentException("messages 不能为空。");
        // Hoist instructions before computing replay keys; caller history must match the original request.
        var instructions = new List<string>();
        foreach (var message in messages.EnumerateArray())
            if (message.ValueKind == JsonValueKind.Object && message.TryGetProperty("role", out var role) && role.ToString() is "system" or "developer") instructions.Add(Content(message));
        if (instructions.Count > 0) request["instructions"] = string.Join("\n\n", instructions);
        var turns = (JsonArray)request["input"]!;
        var pending = new HashSet<string>(StringComparer.Ordinal); var allIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("role", out var role)) throw new ArgumentException("消息缺少 role。");
            string name = role.ToString();
            if (name is "system" or "developer") continue;
            if (name == "tool")
            {
                if (!message.TryGetProperty("tool_call_id", out var id) || id.ValueKind != JsonValueKind.String || !pending.Remove(id.GetString()!)) throw new ArgumentException("工具结果的 tool_call_id 无法对应尚待返回的调用。");
                turns.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = id.GetString(), ["output"] = Content(message) }); continue;
            }
            if (pending.Count > 0) throw new ArgumentException("请先回传上一轮全部工具执行结果。");
            if (name is not ("user" or "assistant")) throw new ArgumentException("不支持的消息角色。");
            if (message.TryGetProperty("tool_calls", out var history) && history.ValueKind != JsonValueKind.Null)
            {
                if (name != "assistant" || history.ValueKind != JsonValueKind.Array || history.GetArrayLength() is < 1 or > 16) throw new ArgumentException("历史工具调用格式无效。");
                var calls = new List<GrokCall>();
                foreach (var call in history.EnumerateArray())
                {
                    var function = call.GetProperty("function"); string id = call.GetProperty("id").GetString()!, tool = function.GetProperty("name").GetString()!;
                    if (call.GetProperty("type").GetString() != "function" || string.IsNullOrWhiteSpace(id) || !allIds.Add(id) || !Name(tool)) throw new ArgumentException("历史工具名称或 ID 无效。");
                    calls.Add(new(id, tool, Arguments(function.GetProperty("arguments").GetString()!))); pending.Add(id);
                }
                foreach (var item in replay.Get(request, calls)) turns.Add(item?.DeepClone());
            }
            else turns.Add(new JsonObject { ["role"] = name, ["content"] = Content(message) });
        }
        if (pending.Count > 0) throw new ArgumentException("历史工具调用缺少执行结果。");
        if (turns.Count == 0) throw new ArgumentException("至少需要一条用户或助手消息。");
        if (input.TryGetProperty("tools", out var tools) && tools.ValueKind != JsonValueKind.Null)
        {
            if (tools.ValueKind != JsonValueKind.Array || tools.GetArrayLength() > 64) throw new ArgumentException("tools 应为数组，最多 64 个工具。");
            var definitions = new JsonArray(); var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tool in tools.EnumerateArray())
            {
                if (tool.GetProperty("type").GetString() != "function") throw new ArgumentException("Chat Completions 仅接受客户端 function 工具。");
                var function = JsonNode.Parse(tool.GetProperty("function").GetRawText())!.AsObject(); string name = function["name"]?.GetValue<string>() ?? "";
                if (!Name(name) || !names.Add(name)) throw new ArgumentException("工具名无效或重复。");
                function["type"] = "function"; definitions.Add(function);
            }
            request["tools"] = definitions;
            if (input.TryGetProperty("tool_choice", out var choice) && choice.ValueKind != JsonValueKind.Null)
            {
                if (choice.ValueKind == JsonValueKind.String && choice.GetString() is "auto" or "none" or "required") request["tool_choice"] = choice.GetString();
                else if (choice.ValueKind == JsonValueKind.Object && choice.GetProperty("type").GetString() == "function" && names.Contains(choice.GetProperty("function").GetProperty("name").GetString()!)) request["tool_choice"] = new JsonObject { ["type"] = "function", ["name"] = choice.GetProperty("function").GetProperty("name").GetString() };
                else throw new ArgumentException("tool_choice 格式无效或工具未声明。");
                if (request["tool_choice"]?.ToString() == "required" && names.Count == 0) throw new ArgumentException("required 需要至少一个工具。");
            }
        }
        else if (input.TryGetProperty("tool_choice", out var absentChoice) && absentChoice.ValueKind != JsonValueKind.Null && absentChoice.ToString() != "none" && absentChoice.ToString() != "auto") throw new ArgumentException("tool_choice 指定了工具，但 tools 为空。");
        if (input.TryGetProperty("parallel_tool_calls", out var multiple)) request["parallel_tool_calls"] = multiple.GetBoolean();
        if (input.TryGetProperty("reasoning_effort", out var effort)) request["reasoning"] = new JsonObject { ["effort"] = effort.GetString() };
        if (input.TryGetProperty("max_tokens", out var maximum) || input.TryGetProperty("max_completion_tokens", out maximum)) request["max_output_tokens"] = maximum.GetInt32();
        if (input.TryGetProperty("temperature", out var temperature)) request["temperature"] = temperature.GetDouble();
        if (input.TryGetProperty("top_p", out var topP)) request["top_p"] = topP.GetDouble();
        if (input.TryGetProperty("response_format", out var format) && format.ValueKind != JsonValueKind.Null) request["text"] = new JsonObject { ["format"] = JsonNode.Parse(format.GetRawText()) };
        return Rewrite(request);
    }
    public static JsonObject Native(JsonElement input)
    {
        var request = JsonNode.Parse(input.GetRawText())!.AsObject(); request["model"] = Model(input);
        if (request["previous_response_id"] != null) throw new ArgumentException("Grok 本地适配使用无存储模式，请发送完整 input 历史，不使用 previous_response_id。");
        if (request["input"] is JsonValue value && value.TryGetValue<string>(out var prompt)) request["input"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt });
        if (request["input"] is not JsonArray turns || turns.Count == 0) throw new ArgumentException("Responses input 不能为空。");
        var instructions = new List<string>();
        if (request["instructions"] is JsonValue existing && existing.TryGetValue<string>(out var text)) instructions.Add(text);
        for (int i = 0; i < turns.Count;)
        {
            var turn = turns[i];
            if (turn?["role"]?.ToString() is "system" or "developer")
            {
                var content = turn["content"];
                instructions.Add(content is JsonArray blocks ? string.Join('\n', blocks.Select(b => b?["text"]?.ToString() ?? "")) : content?.ToString() ?? ""); turns.RemoveAt(i); continue;
            }
            if (turn?["type"]?.ToString() == "reasoning" && string.IsNullOrEmpty(turn["encrypted_content"]?.ToString())) { turns.RemoveAt(i); continue; }
            i++;
        }
        if (instructions.Count > 0) request["instructions"] = string.Join("\n\n", instructions);
        return Rewrite(request);
    }
    private static JsonObject Rewrite(JsonObject request)
    {
        string model = request["model"]!.ToString();
        if (model is "grok-build" or "grok-composer-2.5-fast" or "grok-4.20-0309-reasoning") request.Remove("reasoning");
        else if (request["reasoning"] is JsonObject reasoning && reasoning["effort"] != null)
        {
            string effort = reasoning["effort"]!.ToString();
            bool allowed = effort is "low" or "medium" or "high" || model == "grok-4.7" && effort == "xhigh";
            if (!allowed) throw new ArgumentException($"{model} 不支持指定的 reasoning.effort；该模型不能关闭思考，请使用 low、medium、high" + (model == "grok-4.7" ? " 或 xhigh。" : "。"));
        }
        request["stream"] = true; request["store"] = false;
        var include = request["include"] as JsonArray ?? new JsonArray();
        if (!include.Any(v => v?.ToString() == "reasoning.encrypted_content")) include.Add("reasoning.encrypted_content");
        if (request["include"] == null) request["include"] = include;
        request.Remove("prompt_cache_retention"); return request;
    }
    public static GrokCompletion Complete(JsonObject request, JsonObject response)
    {
        if (response["status"]?.ToString() != "completed" || response["output"] is not JsonArray output) throw new UpstreamException(502, "Grok 回复未完成，本次请求未计为成功。");
        var text = new StringBuilder(); var reasoning = new StringBuilder(); var calls = new List<GrokCall>();
        var allowed = new HashSet<string>((request["tools"] as JsonArray ?? []).Where(t => t?["type"]?.ToString() == "function").Select(t => t?["name"]?.ToString() ?? ""), StringComparer.Ordinal);
        foreach (var item in output)
        {
            if (item?["type"]?.ToString() == "message" && item["content"] is JsonArray blocks)
                foreach (var block in blocks) if (block?["type"]?.ToString() == "output_text") text.Append(block["text"]?.ToString());
            if (item?["type"]?.ToString() == "reasoning" && item["summary"] is JsonArray summaries)
                foreach (var summary in summaries) reasoning.Append(summary?["text"]?.ToString());
            if (item?["type"]?.ToString() != "function_call") continue;
            string id = item["call_id"]?.ToString() ?? "", name = item["name"]?.ToString() ?? "", arguments = item["arguments"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(id) || !allowed.Contains(name) || calls.Any(c => c.Id == id)) throw new UpstreamException(502, "Grok 返回了无效或未声明的工具调用。");
            try { arguments = Arguments(arguments); } catch (Exception ex) when (ex is JsonException or ArgumentException) { throw new UpstreamException(502, "Grok 返回的工具参数无效，未发送给客户端执行。"); }
            calls.Add(new(id, name, arguments));
        }
        if (calls.Count > 16 || request["parallel_tool_calls"]?.ToString() == "false" && calls.Count > 1) throw new UpstreamException(502, "Grok 返回的工具调用数量超过允许范围。");
        var choice = request["tool_choice"];
        if (choice?.ToString() == "none" && calls.Count > 0 || choice?.ToString() == "required" && calls.Count == 0 || choice is JsonObject selected && (calls.Count == 0 || calls.Any(c => c.Name != selected["name"]?.ToString()))) throw new UpstreamException(502, "Grok 返回的工具调用未遵循本次 tool_choice。");
        if (text.Length == 0 && calls.Count == 0) throw new UpstreamException(502, "Grok 未返回文本或工具调用。");
        return new(text.ToString(), reasoning.ToString(), calls, response);
    }
}
