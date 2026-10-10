using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cuelify.Core.Translation;

namespace Cuelify.Infrastructure.Translation.Local;

// 预览和生成共用服务端模板、分词及同一份最终提示词。
public sealed class LlamaServerProtocol(HttpClient http, uint contextSize, int maximumTokens)
{
    public async Task ValidateCapacityAsync(int concurrency, CancellationToken token)
    {
        using var response = await http.GetAsync("props", token);
        CheckResponse(response);
        var json = await ReadJsonAsync(response, token);
        if (!json.TryGetProperty("total_slots", out var slots) || slots.GetInt32() < concurrency ||
            !json.TryGetProperty("default_generation_settings", out var settings) ||
            !settings.TryGetProperty("n_ctx", out var context) || context.GetInt64() < contextSize)
            throw new LocalTranslationException("ServerCapacity", "本地服务的并发数或每条上下文长度不足，请检查运行包版本和显存。");
    }

    public Task<PreparedInferencePrompt> PrepareAsync(TranslationRequest request, CancellationToken token) =>
        EmbeddedPromptBudget.PrepareAsync(request, RenderAsync, contextSize, maximumTokens, token);

    private async Task<(string Prompt, int Tokens)> RenderAsync(IReadOnlyList<PromptMessage> messages, CancellationToken token)
    {
        using var rendered = await http.PostAsJsonAsync("apply-template", new
        { messages = messages.Select(message => new { role = message.Role, content = message.Content }), add_generation_prompt = true }, token);
        CheckResponse(rendered);
        var template = await ReadJsonAsync(rendered, token);
        if (!template.TryGetProperty("prompt", out var value) || value.ValueKind != JsonValueKind.String)
            throw new LocalTranslationException("ServerIncompatible", "本地服务没有返回有效的模型提示词模板。");
        var prompt = value.GetString()!;
        using var tokenized = await http.PostAsJsonAsync("tokenize", new { content = prompt, add_special = true, parse_special = true }, token);
        CheckResponse(tokenized);
        var tokens = await ReadJsonAsync(tokenized, token);
        if (!tokens.TryGetProperty("tokens", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new LocalTranslationException("ServerIncompatible", "本地服务没有返回有效的 token 信息。");
        return (prompt, items.GetArrayLength());
    }

    public async Task<LocalCompletion> CompleteAsync(string prompt, IProgress<int>? progress, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "completion")
        {
            Content = JsonContent.Create(new
            {
                prompt, stream = true, n_predict = maximumTokens, temperature = .7, top_p = .6, top_k = 20,
                repeat_penalty = 1.05, repeat_last_n = 64, min_p = 0, seed = 42,
                samplers = new[] { "penalties", "top_k", "top_p", "temperature" },
                cache_prompt = false
            })
        };
        // 流式连接在取消时立即关闭，使 llama-server 撤销该请求并归还 slot。
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        CheckResponse(response);
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            throw new LocalTranslationException("ServerIncompatible", "本地服务不支持所需的流式生成接口，请更新 llama.cpp。");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var output = new StringBuilder();
        var events = 0;
        while (await reader.ReadLineAsync(token) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var data = line[6..];
            if (data == "[DONE]") break;
            using var json = JsonDocument.Parse(data);
            var value = json.RootElement;
            if (value.TryGetProperty("error", out _))
                throw new LocalTranslationException("ServerRequest", "本地服务未能生成译文，请检查模型设置和显存。");
            if (value.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                output.Append(content.GetString());
            if (output.Length > 1_048_576) throw new InvalidDataException("本地服务返回的译文过长。");
            if (value.TryGetProperty("stop", out var stop) && stop.ValueKind == JsonValueKind.True)
            {
                var count = value.TryGetProperty("tokens_predicted", out var predicted) ? predicted.GetInt32() :
                    value.TryGetProperty("timings", out var timings) && timings.TryGetProperty("predicted_n", out predicted) ? predicted.GetInt32() : 0;
                var stopType = value.TryGetProperty("stop_type", out var type) ? type.GetString() ?? "none" :
                    value.TryGetProperty("stopped_eos", out var eos) && eos.ValueKind == JsonValueKind.True ? "eos" : "none";
                var truncated = value.TryGetProperty("truncated", out var truncation) && truncation.ValueKind == JsonValueKind.True;
                progress?.Report(count);
                return new(output.ToString(), count, stopType, truncated);
            }
            progress?.Report(++events);
        }
        throw new InvalidDataException("本地服务在生成完成前断开连接，请重试。");
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        return json.RootElement.Clone();
    }

    private static void CheckResponse(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var category = response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest ? "ServerIncompatible" : "ServerRequest";
        throw new LocalTranslationException(category, $"本地服务请求失败（HTTP {(int)response.StatusCode}），请检查运行包版本和模型设置。");
    }
}
