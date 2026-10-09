using Cuelify.Core.Translation;

namespace Cuelify.Infrastructure.Translation.Local;

public static class EmbeddedPromptBudget
{
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
            var context = current.PromptContext;
            if (context is null || (context.Before.Count == 0 && context.After.Count == 0))
                throw new InvalidDataException("当前字幕和提示词超过上下文长度，请调大上下文长度或缩短提示词。");
            var before = context.Before;
            var after = context.After;
            if (before.Count > after.Count) before = before.Skip(1).ToArray();
            else after = after.Take(after.Count - 1).ToArray();
            current = new PromptBuilder().Build(context.Profile, context.Settings, request.Cues, before, after);
            removed++;
        }
    }
}
