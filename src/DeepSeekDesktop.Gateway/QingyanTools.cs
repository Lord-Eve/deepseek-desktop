using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeepSeekDesktop.Gateway;

public sealed record QingyanToolCall(string Id, string Name, string Arguments);

// Prompt-based function calling for the web channel. Tools are executed only by the API client.
public sealed class QingyanTools
{
    private const string Open = "[function_calls]", Close = "[/function_calls]";
    private readonly Dictionary<string, JsonElement> tools = new(StringComparer.Ordinal);
    private string choice = "auto", forcedName = "";
    private bool parallel = true;
    public bool Enabled => tools.Count > 0 && choice != "none";
    public string Prompt { get; private set; } = "";

    public static QingyanTools Prepare(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException("请求必须是 JSON 对象。");
        if (!input.TryGetProperty("model", out var model) || model.ToString() != "qingyan-default") throw new ArgumentException("清言当前支持的模型为 qingyan-default，代表网页端默认模式。");
        if (input.TryGetProperty("functions", out _) || input.TryGetProperty("function_call", out _)) throw new ArgumentException("清言工具调用请使用 tools 和 tool_choice 协议，不支持旧 functions 协议。");
        if (input.TryGetProperty("response_format", out var format) && format.ValueKind != JsonValueKind.Null && (format.ValueKind != JsonValueKind.Object || !format.TryGetProperty("type", out var type) || type.ToString() != "text")) throw new ArgumentException("清言当前版本暂不支持结构化输出约束。");
        if (input.TryGetProperty("n", out var count) && (!count.TryGetInt32(out int number) || number != 1)) throw new ArgumentException("清言每次仅支持一个回答。");
        var plan = new QingyanTools();
        plan.ReadTools(input);
        if (!input.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() == 0) throw new ArgumentException("messages 不能为空。");
        var transcript = new StringBuilder();
        var calls = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("role", out var role)) throw new ArgumentException("消息缺少有效的 role。");
            string name = role.ToString();
            if (name == "tool")
            {
                if (!message.TryGetProperty("tool_call_id", out var callId) || callId.ValueKind != JsonValueKind.String || !pending.Remove(callId.GetString()!)) throw new ArgumentException("工具结果的 tool_call_id 未对应尚待返回的调用，或结果重复。");
                string id = callId.GetString()!;
                transcript.Append("[TOOL_RESULT for ").Append(id).Append("] (").Append(calls[id]).Append(")\n").Append(Content(message, false)).Append("\n\n");
                continue;
            }
            if (pending.Count > 0) throw new ArgumentException("继续对话前，请先提供上一轮全部工具调用的结果。");
            if (name is not ("system" or "developer" or "user" or "assistant")) throw new ArgumentException("不支持的消息角色。");
            bool hasCalls = message.TryGetProperty("tool_calls", out var history) && history.ValueKind != JsonValueKind.Null;
            if (hasCalls && name != "assistant") throw new ArgumentException("只有 assistant 消息可以包含 tool_calls。");
            string text = Content(message, name == "assistant" && hasCalls);
            if (messages.GetArrayLength() > 1 || hasCalls) transcript.Append('[').Append(name).Append("]\n");
            transcript.Append(text);
            if (hasCalls)
            {
                if (history.ValueKind != JsonValueKind.Array || history.GetArrayLength() is < 1 or > 16) throw new ArgumentException("历史 tool_calls 必须为 1–16 个调用。");
                transcript.Append('\n').Append(Open).Append('\n');
                foreach (var call in history.EnumerateArray())
                {
                    if (call.ValueKind != JsonValueKind.Object || !call.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String || !ValidId(idValue.GetString()!) || !call.TryGetProperty("type", out var kind) || kind.ToString() != "function" || !call.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object || !function.TryGetProperty("name", out var toolName) || toolName.ValueKind != JsonValueKind.String || !ValidName(toolName.GetString()!) || !function.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.String) throw new ArgumentException("历史工具调用格式无效。");
                    string id = idValue.GetString()!, tool = toolName.GetString()!;
                    if (!calls.TryAdd(id, tool)) throw new ArgumentException("历史工具调用 ID 重复。");
                    using var parsed = JsonDocument.Parse(arguments.GetString()!);
                    if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("历史工具参数必须是 JSON 对象。");
                    pending.Add(id);
                    transcript.Append("[call:").Append(tool).Append(']').Append(JsonSerializer.Serialize(parsed.RootElement)).Append("[/call]\n");
                }
                transcript.Append(Close);
            }
            if (messages.GetArrayLength() > 1 || hasCalls) transcript.Append("\n\n");
        }
        if (pending.Count > 0) throw new ArgumentException("历史工具调用缺少执行结果。");
        if (string.IsNullOrWhiteSpace(transcript.ToString())) throw new ArgumentException("消息内容不能为空。");
        if (!plan.Enabled) { plan.Prompt = transcript.ToString(); return plan; }
        var instructions = new StringBuilder("你正在继续下面的对话。客户端提供了外部工具，工具由客户端执行。需要搜索时应调用列表中的搜索工具，不要用清言内置搜索替代，也不要声称已经执行未返回结果的调用。\n\n");
        instructions.Append(transcript).Append("\n工具调用协议：\n需要工具时，整条回复只输出以下格式的完整块，不要放在 Markdown 代码块里，不要输出旁白：\n")
            .Append(Open).Append("\n[call:列表中的实际工具名]{\"参数名\":\"参数值\"}[/call]\n").Append(Close)
            .Append("\n工具名必须与目录完全一致。参数必须为 JSON 对象，遵循参数 schema，不得漏掉 required 字段。一次多个调用可在同一个块内使用多对 [call:...]...[/call]。工具结果以 [TOOL_RESULT for 调用ID] (工具名) 回传；收到结果后再回答或发起下一次调用。不要自行编造工具结果。\n");
        if (!plan.parallel) instructions.Append("本次最多调用一个工具。\n");
        if (plan.choice == "required") instructions.Append("本次必须调用至少一个工具，不能直接给出最终答案。\n");
        if (plan.forcedName.Length > 0) instructions.Append("本次必须调用指定工具：").Append(plan.forcedName).Append("，不可调用其他工具。\n");
        instructions.Append("可用工具目录（function 的 name、description、parameters）：\n");
        foreach (var tool in plan.tools.Values) instructions.AppendLine(JsonSerializer.Serialize(tool, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        instructions.Append("\n现在继续最后一条消息。需要工具就输出完整调用块；不需要工具时直接回答用户，不要复述对话或工具说明。\n[assistant]\n");
        if (instructions.Length > 1024 * 1024) throw new ArgumentException("工具与历史消息合并后过长，请减少工具描述或历史。");
        plan.Prompt = instructions.ToString();
        return plan;
    }

    private void ReadTools(JsonElement input)
    {
        int budget = 0;
        if (input.TryGetProperty("tools", out var definitions) && definitions.ValueKind != JsonValueKind.Null)
        {
            if (definitions.ValueKind != JsonValueKind.Array || definitions.GetArrayLength() > 64) throw new ArgumentException("tools 应为数组，最多支持 64 个工具。");
            foreach (var definition in definitions.EnumerateArray())
            {
                if (definition.ValueKind != JsonValueKind.Object || !definition.TryGetProperty("type", out var type) || type.ToString() != "function" || !definition.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object || !function.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || !ValidName(name.GetString()!)) throw new ArgumentException("工具需要 type=function 和有效的 function.name（1–64 位字母、数字、下划线或连字符）。");
                if (function.TryGetProperty("parameters", out var schema) && schema.ValueKind != JsonValueKind.Object) throw new ArgumentException("工具 parameters 必须为 JSON Schema 对象。");
                if (!tools.TryAdd(name.GetString()!, function.Clone())) throw new ArgumentException("工具名称不能重复。");
                budget += function.GetRawText().Length;
                if (budget > 65536) throw new ArgumentException("工具定义超过 65536 字符，请减少本次暴露的工具或描述长度。");
            }
        }
        if (input.TryGetProperty("tool_choice", out var selected) && selected.ValueKind != JsonValueKind.Null)
        {
            if (selected.ValueKind == JsonValueKind.String && selected.GetString() is "none" or "auto" or "required") choice = selected.GetString()!;
            else if (selected.ValueKind == JsonValueKind.Object && selected.TryGetProperty("type", out var type) && type.ToString() == "function" && selected.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object && function.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && tools.ContainsKey(name.GetString()!)) { choice = "required"; forcedName = name.GetString()!; }
            else throw new ArgumentException("tool_choice 应为 auto、none、required，或本次工具目录中的指定 function。");
        }
        if (choice == "required" && tools.Count == 0) throw new ArgumentException("tool_choice 要求调用工具，但 tools 为空。");
        if (input.TryGetProperty("parallel_tool_calls", out var multiple))
        {
            if (multiple.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("parallel_tool_calls 应为布尔值。");
            parallel = multiple.GetBoolean();
        }
    }

    public IReadOnlyList<QingyanToolCall> Parse(string reply)
    {
        if (!Enabled) return [];
        string text = reply.Trim();
        if (!text.StartsWith(Open, StringComparison.Ordinal))
        {
            if (choice == "required" || text.Contains(Open, StringComparison.Ordinal) || text.Contains("[call:", StringComparison.Ordinal) || text.Contains(Close, StringComparison.Ordinal)) throw Failure("清言未按要求返回完整工具调用块，请重试或切换模型通道。");
            return [];
        }
        if (!text.EndsWith(Close, StringComparison.Ordinal)) throw Failure("清言工具调用块不完整，未发送给客户端执行。");
        var calls = new List<QingyanToolCall>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int position = Open.Length, end = text.Length - Close.Length;
        void SkipSpace() { while (position < end && char.IsWhiteSpace(text[position])) position++; }
        while (position < end)
        {
            SkipSpace(); if (position == end) break;
            if (!text.AsSpan(position, end - position).StartsWith("[call:", StringComparison.Ordinal)) throw Failure("清言工具调用格式无效，未执行。");
            int nameEnd = text.IndexOf(']', position + 6);
            if (nameEnd < 0 || nameEnd >= end) throw Failure("清言工具名不完整。");
            string name = text[(position + 6)..nameEnd].Trim();
            if (!tools.TryGetValue(name, out var definition) || forcedName.Length > 0 && name != forcedName) throw Failure("清言返回了工具目录之外或未按指定选择的工具，未执行。");
            position = nameEnd + 1; SkipSpace();
            int jsonEnd = ObjectEnd(text, position, end);
            string arguments;
            try
            {
                using var json = JsonDocument.Parse(text[position..jsonEnd]);
                if (json.RootElement.ValueKind != JsonValueKind.Object) throw Failure("工具参数必须为 JSON 对象。");
                if (definition.TryGetProperty("parameters", out var schema) && !Matches(json.RootElement, schema)) throw Failure("清言返回的工具参数不符合声明的 schema，未发送给客户端执行。");
                arguments = JsonSerializer.Serialize(json.RootElement);
            }
            catch (JsonException) { throw Failure("清言返回了无效的工具参数 JSON，未发送给客户端执行。"); }
            position = jsonEnd; SkipSpace();
            if (!text.AsSpan(position, end - position).StartsWith("[/call]", StringComparison.Ordinal)) throw Failure("清言工具调用缺少结束标记，未执行。");
            position += "[/call]".Length;
            if (seen.Add(name + "\0" + arguments)) calls.Add(new("call_" + Guid.NewGuid().ToString("N"), name, arguments));
            if (calls.Count > 16 || !parallel && calls.Count > 1) throw Failure("清言返回的工具调用数量超过本次允许范围。");
        }
        if (calls.Count == 0) throw Failure("清言返回了空工具调用块。");
        return calls;
    }

    private static int ObjectEnd(string text, int start, int end)
    {
        if (start >= end || text[start] != '{') throw Failure("清言工具参数缺少 JSON 对象。");
        int depth = 0; bool quoted = false, escaped = false;
        for (int i = start; i < end; i++)
        {
            char c = text[i];
            if (quoted) { if (escaped) escaped = false; else if (c == '\\') escaped = true; else if (c == '"') quoted = false; continue; }
            if (c == '"') quoted = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i + 1;
        }
        throw Failure("清言工具参数 JSON 不完整，未执行。");
    }

    private static string Content(JsonElement message, bool allowEmpty)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind == JsonValueKind.Null)
        {
            if (allowEmpty) return "";
            throw new ArgumentException("消息缺少文本 content。");
        }
        if (content.ValueKind == JsonValueKind.String) return content.GetString()!;
        if (content.ValueKind != JsonValueKind.Array) throw new ArgumentException("消息内容必须为文本。");
        var text = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("type", out var kind) || kind.ToString() != "text" || !block.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String) throw new ArgumentException("清言当前版本只支持文本内容，不支持图片、音频或文件。");
            text.Append(value.GetString());
        }
        return text.ToString();
    }

    // Validate the JSON Schema subset commonly emitted by MCP tools, without coercing or repairing args.
    private static bool Matches(JsonElement value, JsonElement schema, int depth = 0)
    {
        if (depth > 32) return false;
        if (schema.ValueKind == JsonValueKind.True) return true;
        if (schema.ValueKind == JsonValueKind.False) return false;
        if (schema.ValueKind != JsonValueKind.Object) return false;
        if (schema.TryGetProperty("type", out var type))
        {
            bool IsType(string? kind) => kind switch { "object" => value.ValueKind == JsonValueKind.Object, "array" => value.ValueKind == JsonValueKind.Array, "string" => value.ValueKind == JsonValueKind.String, "number" => value.ValueKind == JsonValueKind.Number, "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var n) && n == decimal.Truncate(n), "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False, "null" => value.ValueKind == JsonValueKind.Null, _ => false };
            if (type.ValueKind == JsonValueKind.String ? !IsType(type.GetString()) : type.ValueKind != JsonValueKind.Array || !type.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && IsType(t.GetString()))) return false;
        }
        bool Equal(JsonElement left, JsonElement right) => left.ValueKind == right.ValueKind && (left.ValueKind == JsonValueKind.String ? left.GetString() == right.GetString() : left.GetRawText() == right.GetRawText());
        if (schema.TryGetProperty("enum", out var options) && (options.ValueKind != JsonValueKind.Array || !options.EnumerateArray().Any(v => Equal(value, v)))) return false;
        if (schema.TryGetProperty("const", out var constant) && !Equal(value, constant)) return false;
        foreach (string key in new[] { "anyOf", "oneOf", "allOf" })
        {
            if (!schema.TryGetProperty(key, out var alternatives)) continue;
            if (alternatives.ValueKind != JsonValueKind.Array) return false;
            int matched = alternatives.EnumerateArray().Count(s => Matches(value, s, depth + 1));
            if (key == "anyOf" && matched == 0 || key == "oneOf" && matched != 1 || key == "allOf" && matched != alternatives.GetArrayLength()) return false;
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            bool hasProperties = schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object;
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) return false;
                if (hasProperties && properties.TryGetProperty(property.Name, out var member)) { if (!Matches(property.Value, member, depth + 1)) return false; }
                else if (schema.TryGetProperty("additionalProperties", out var extra) && !Matches(property.Value, extra, depth + 1)) return false;
            }
            if (schema.TryGetProperty("required", out var required) && (required.ValueKind != JsonValueKind.Array || required.EnumerateArray().Any(r => r.ValueKind != JsonValueKind.String || !names.Contains(r.GetString()!)))) return false;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            if (schema.TryGetProperty("minItems", out var min) && value.GetArrayLength() < min.GetInt32() || schema.TryGetProperty("maxItems", out var max) && value.GetArrayLength() > max.GetInt32()) return false;
            if (schema.TryGetProperty("items", out var items) && value.EnumerateArray().Any(v => !Matches(v, items, depth + 1))) return false;
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            string text = value.GetString()!;
            if (schema.TryGetProperty("minLength", out var min) && text.EnumerateRunes().Count() < min.GetInt32() || schema.TryGetProperty("maxLength", out var max) && text.EnumerateRunes().Count() > max.GetInt32()) return false;
        }
        if (value.ValueKind == JsonValueKind.Number)
        {
            double number = value.GetDouble();
            if (schema.TryGetProperty("minimum", out var min) && number < min.GetDouble() || schema.TryGetProperty("maximum", out var max) && number > max.GetDouble() || schema.TryGetProperty("exclusiveMinimum", out var exMin) && number <= exMin.GetDouble() || schema.TryGetProperty("exclusiveMaximum", out var exMax) && number >= exMax.GetDouble()) return false;
        }
        return true;
    }

    private static bool ValidName(string value) => Regex.IsMatch(value, "^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);
    private static bool ValidId(string value) => value.Length is > 0 and <= 128 && value.All(c => c is >= '!' and <= '~' && c is not ('[' or ']'));
    private static UpstreamException Failure(string message) => new(502, message);
}
