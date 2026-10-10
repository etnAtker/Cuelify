using Cuelify.Core.Translation;

namespace Cuelify.Infrastructure.Translation.Local;

public sealed class PromptCapacityException() : Exception("字幕和提示词超过上下文长度，请调大上下文长度或缩短提示词。");

public static class EmbeddedPromptBudget
{
    public static async Task<PreparedInferencePrompt> PrepareAsync(TranslationRequest request,
        Func<IReadOnlyList<PromptMessage>, CancellationToken, Task<(string Prompt, int Tokens)>> prepare,
        uint contextSize, int maximumTokens, CancellationToken token)
    {
        var current = request;
        var removed = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var (prompt, tokens) = await prepare(current.Messages, token);
            if (tokens < 0) throw new InvalidDataException("本地服务返回的 token 数无效。");
            if ((long)tokens + maximumTokens <= contextSize) return new(current.Messages, prompt, tokens, removed);
            current = RemoveReference(current, request);
            removed++;
        }
    }

    public static PreparedInferencePrompt Prepare(TranslationRequest request, Func<string, int> tokenize,
        Func<IReadOnlyList<PromptMessage>, string> render, uint contextSize, int maximumTokens)
    {
        var current = request;
        var removed = 0;
        while (true)
        {
            var prompt = render(current.Messages);
            var tokens = tokenize(prompt);
            if ((long)tokens + maximumTokens <= contextSize) return new(current.Messages, prompt, tokens, removed);
            current = RemoveReference(current, request);
            removed++;
        }
    }

    private static TranslationRequest RemoveReference(TranslationRequest current, TranslationRequest original)
    {
        var context = current.PromptContext;
        if (context is null || (context.Before.Count == 0 && context.After.Count == 0))
            throw new PromptCapacityException();
        var before = context.Before;
        var after = context.After;
        if (before.Count > after.Count) before = before.Skip(1).ToArray();
        else after = after.Take(after.Count - 1).ToArray();
        return new PromptBuilder().Build(context.Profile, context.Settings, original.Cues, before, after);
    }
}
