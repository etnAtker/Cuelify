using System.Text.Json;
using System.Text.Json.Nodes;
using Cuelify.Core.Translation;
using Cuelify.Infrastructure.Storage;

namespace Cuelify.Infrastructure.Translation;

public enum ReasoningCapability { Unsupported, ProviderSpecific, ReasoningEffort }
public sealed record CloudTranslationOptions
{
    public string BaseUrl { get; init; } = "https://api.openai.com/v1";
    public string Model { get; init; } = "";
    public double? Temperature { get; init; }
    public double? TopP { get; init; }
    public int? MaximumTokens { get; init; } = 2048;
    public double? FrequencyPenalty { get; init; }
    public double? PresencePenalty { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
    public ReasoningCapability ReasoningCapability { get; init; }
    public string? ReasoningEffort { get; init; }
    public string? AdditionalParametersJson { get; init; }

    public Uri Endpoint()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("请输入有效的 HTTP 或 HTTPS 服务地址，地址中不要包含密钥、查询参数或 # 片段。");
        var builder = new UriBuilder(uri);
        var path = builder.Path.TrimEnd('/');
        builder.Path = path.EndsWith("/chat/completions", StringComparison.Ordinal) ? path : path + "/chat/completions";
        return builder.Uri;
    }

    public void Validate()
    {
        _ = Endpoint();
        if (string.IsNullOrWhiteSpace(Model)) throw new ArgumentException("请填写翻译模型名称。");
        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromHours(1)) throw new ArgumentException("请求超时时间必须在 0～3600 秒之间，且大于 0。");
        if (!Valid(Temperature, 0, 2)) throw new ArgumentException("温度必须在 0～2 之间。");
        if (!Valid(TopP, double.Epsilon, 1)) throw new ArgumentException("采样范围（top_p）必须大于 0 且不超过 1。");
        if (!Valid(FrequencyPenalty, -2, 2)) throw new ArgumentException("重复惩罚必须在 -2～2 之间。");
        if (!Valid(PresencePenalty, -2, 2)) throw new ArgumentException("新词倾向必须在 -2～2 之间。");
        if (MaximumTokens is <= 0) throw new ArgumentException("最大输出长度必须大于 0，或留空使用服务默认值。");
        if (!Enum.IsDefined(ReasoningCapability) ||
            (ReasoningEffort is not null && (ReasoningCapability != ReasoningCapability.ReasoningEffort ||
                ReasoningEffort is not ("none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max"))))
            throw new ArgumentException("思考参数不匹配，请检查思考参数类型及该模型支持的思考强度。");
        _ = AdditionalParameters();
    }

    public JsonObject AdditionalParameters()
    {
        if (string.IsNullOrWhiteSpace(AdditionalParametersJson)) return new();
        JsonNode? node;
        try { node = JsonNode.Parse(AdditionalParametersJson); }
        catch (JsonException) { throw new ArgumentException("高级参数必须是合法 JSON 对象。"); }
        if (node is not JsonObject value) throw new ArgumentException("高级参数必须是 JSON 对象。");
        using var document = JsonDocument.Parse(AdditionalParametersJson);
        ValidateObject(document.RootElement);
        var reserved = new[] { "model", "messages", "stream", "n", "temperature", "top_p", "max_tokens", "max_completion_tokens", "frequency_penalty", "presence_penalty", "reasoning_effort" };
        var controlled = value.FirstOrDefault(pair => reserved.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)).Key;
        if (controlled is not null) throw new ArgumentException($"请从自定义参数中移除 {controlled}，并使用对应的设置项调整。");
        if (value.ContainsKey("thinking") || value.ContainsKey("enable_thinking"))
            if (ReasoningCapability != ReasoningCapability.ProviderSpecific) throw new ArgumentException("使用自定义思考参数时，请将思考参数类型设为“服务自定义参数”。");
        return value;
    }

    private static void ValidateObject(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new ArgumentException($"自定义参数中的 {property.Name} 重复，请只保留一项。");
                if (property.Name.ToLowerInvariant() is "api_key" or "authorization" or "xi-api-key" or "headers")
                    throw new ArgumentException("自定义参数只用于请求正文。请移除密钥或请求头字段，将 API 密钥填写在密钥栏中。");
                ValidateObject(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) ValidateObject(item);
    }
    private static bool Valid(double? value, double minimum, double maximum) => value is null ||
        (double.IsFinite(value.Value) && value >= minimum && value <= maximum);
}

public sealed record DeepSeekThinkingOptions(bool Enabled = false, string? Effort = null)
{
    public void Validate()
    {
        if ((!Enabled && Effort is not null) || (Effort is not null && Effort is not ("low" or "high" or "max")))
            throw new ArgumentException("DeepSeek 关闭思考时不能指定强度；启用时只接受 low/high/max。");
    }
}

public static class CloudRequestBuilder
{
    public static JsonObject Build(CloudTranslationOptions options, IReadOnlyList<PromptMessage> messages, DeepSeekThinkingOptions? deepSeek = null)
    {
        options.Validate();
        if (messages.Count == 0 || messages.Any(message => message.Role is not ("system" or "user") || string.IsNullOrWhiteSpace(message.Content)))
            throw new ArgumentException("翻译消息无效。");
        var result = options.AdditionalParameters();
        if (deepSeek is not null)
        {
            deepSeek.Validate();
            if (options.Endpoint().Host != "api.deepseek.com" || options.Endpoint().Scheme != "https") throw new ArgumentException("DeepSeek 官方路径必须使用官方 HTTPS 端点。");
            if (result.ContainsKey("thinking") || result.ContainsKey("enable_thinking") || options.ReasoningEffort is not null)
                throw new ArgumentException("请通过“启用思考模式”和“思考强度”设置 DeepSeek，移除自定义参数中的思考字段。");
            result["thinking"] = new JsonObject { ["type"] = deepSeek.Enabled ? "enabled" : "disabled" };
            if (deepSeek.Enabled)
            {
                if (deepSeek.Effort is not null) result["reasoning_effort"] = deepSeek.Effort;
                if (options.TopP is { } topP)
                {
                    if (topP < .95) throw new ArgumentException("DeepSeek 思考模式的采样范围（top_p）必须在 0.95～1 之间。");
                    result["top_p"] = topP;
                }
            }
        }
        else
        {
            Add(result, "top_p", options.TopP);
            if (options.ReasoningEffort is not null) result["reasoning_effort"] = options.ReasoningEffort;
        }
        if (deepSeek is not { Enabled: true })
        {
            Add(result, "temperature", options.Temperature);
            Add(result, "frequency_penalty", options.FrequencyPenalty);
            Add(result, "presence_penalty", options.PresencePenalty);
        }
        if (options.MaximumTokens is { } tokens) result["max_tokens"] = tokens;
        result["model"] = options.Model;
        result["stream"] = false;
        result["messages"] = new JsonArray(messages.Select(message => (JsonNode)new JsonObject { ["role"] = message.Role, ["content"] = message.Content }).ToArray());
        return result;
    }
    public static string Identity(CloudTranslationOptions options, DeepSeekThinkingOptions? thinking = null)
    {
        var body = Build(options, [new("user", "（配置签名，不发送）")], thinking);
        body.Remove("messages");
        return AtomicFile.Hash(new { Provider = thinking is null ? "openai-compatible" : "deepseek-official", Endpoint = options.Endpoint().AbsoluteUri, Body = body });
    }
    private static void Add(JsonObject body, string name, double? value) { if (value is { } number) body[name] = number; }
}
