using LLama.Native;
using LLama.Sampling;

namespace Cuelify.Infrastructure.Translation.Local;

public sealed class TranslationSamplingPipeline : DefaultSamplingPipeline
{
    public int GeneratedTokens { get; private set; }
    public bool EndOfGenerationObserved { get; private set; }

    public TranslationSamplingPipeline()
    {
        Temperature = 0.7f;
        TopP = 0.6f;
        TopK = 20;
        RepeatPenalty = 1.05f;
        MinP = 0;
        Seed = 42;
    }

    public override LLamaToken Sample(SafeLLamaContextHandle ctx, int index)
    {
        var token = base.Sample(ctx, index);
        if (token.IsEndOfGeneration(ctx.Vocab))
            EndOfGenerationObserved = true;
        else
            GeneratedTokens++;
        return token;
    }
}
