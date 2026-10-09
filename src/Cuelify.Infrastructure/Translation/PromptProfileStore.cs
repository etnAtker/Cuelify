using Cuelify.Core.Translation;
using Cuelify.Infrastructure.Storage;

namespace Cuelify.Infrastructure.Translation;

public static class PromptProfileStore
{
    public static Task SaveAsync(string path, PromptProfile profile, CancellationToken cancellationToken)
    {
        PromptBuilder.Validate(profile);
        return AtomicFile.WriteJsonAsync(path, profile, cancellationToken);
    }
    public static async Task<PromptProfile> LoadAsync(string path, CancellationToken cancellationToken)
    {
        var profile = await AtomicFile.ReadJsonAsync<PromptProfile>(path, cancellationToken) ?? throw new FileNotFoundException("提示词模板不存在。", path);
        PromptBuilder.Validate(profile);
        return profile;
    }
}
