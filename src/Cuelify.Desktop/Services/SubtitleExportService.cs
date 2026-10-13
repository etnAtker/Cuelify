using Cuelify.Core.Subtitles;
using Cuelify.Infrastructure.Storage;

namespace Cuelify.Desktop.Services;

internal sealed class SubtitleExportService(IWindowDialogs dialogs)
{
    public async Task<string?> ExportAsync(string inputPath, string language, IReadOnlyList<SubtitleCue> cues,
        Func<bool> canExport, Func<IDisposable> feedback, bool previous = false)
    {
        // 在打开对话框前冻结输入，导航或设置变化不能替换待导出的版本。
        var content = SrtSerializer.SerializeTranslated(cues);
        var safeLanguage = string.Concat(language.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        var path = await dialogs.SaveSrtAsync($"{Path.GetFileNameWithoutExtension(inputPath)}.{safeLanguage}{(previous ? ".previous" : "")}.srt");
        if (path is null) return null;
        if (!Path.GetExtension(path).Equals(".srt", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFullPath(path).Equals(Path.GetFullPath(inputPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择 SRT 输出路径，不能覆盖原媒体。");
        if (File.Exists(path) && !await dialogs.ConfirmAsync("目标 SRT 已存在，确认覆盖？")) return null;
        if (!canExport()) throw new ArgumentException("此版本无法导出，请重新选择完整译文版本。");
        using var operation = feedback();
        await AtomicFile.WriteTextAsync(path, content, true, CancellationToken.None);
        return path;
    }
}
