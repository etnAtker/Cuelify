using System.Collections.ObjectModel;
using Avalonia.Controls.Selection;
using Cuelify.Core.Projects;
using Cuelify.Core.Subtitles;
using Cuelify.Core.Translation;

namespace Cuelify.Desktop.ViewModels;

// 当前结果与历史预览分别持有实例，共用行投影、选择保持与交错通知合并。
internal sealed class SubtitleRowsViewModel
{
    public ObservableCollection<CueRow> Rows { get; } = [];
    public SelectionModel<CueRow> Selection { get; } = new() { SingleSelect = false };
    public SubtitleRowsViewModel() => Selection.Source = Rows;

    public static bool IsComplete(IEnumerable<SubtitleCue> cues)
    {
        var values = cues.ToArray();
        return values.Length > 0 && values.All(c => AlignmentValidator.IsValid(c.TranslatedText ?? ""));
    }
    public static CueRow[] Project(IReadOnlyList<SubtitleCue> cues, StageRecord stage, IReadOnlyList<SourceWordRange>? ranges = null) =>
        cues.Select((cue, index) => new CueRow(cue, index + 1)
        {
            State = stage.FailedUnits.Contains(cue.Id) ? CueTranslationState.Failed : stage.UnitStates.GetValueOrDefault(cue.Id) switch
            { ProcessingState.Running => CueTranslationState.Translating, ProcessingState.Cancelled => CueTranslationState.Cancelled,
                ProcessingState.Failed => CueTranslationState.Failed, _ => CueTranslationState.Waiting },
            WordRange = ranges?.Count > index ? $"识别词 {ranges[index].First + 1}–{ranges[index].Last + 1}" : "",
            FailureReason = stage.UnitErrors.GetValueOrDefault(cue.Id, "")
        }).ToArray();

    public void Set(IReadOnlyList<CueRow> rows)
    {
        var ids = Selection.SelectedItems.Select(row => row!.Id).ToHashSet(StringComparer.Ordinal);
        var sameSequence = Rows.Count == rows.Count && Rows.Select(r => r.Id).SequenceEqual(rows.Select(r => r.Id));
        Selection.Clear();
        if (!sameSequence) Rows.Clear();
        for (var index = 0; index < rows.Count; index++)
        {
            if (!sameSequence) Rows.Add(rows[index]);
            else if (Rows[index].Cue != rows[index].Cue || Rows[index].WordRange != rows[index].WordRange) Rows[index] = rows[index];
            else { Rows[index].State = rows[index].State; Rows[index].FailureReason = rows[index].FailureReason; }
        }
        for (var index = 0; index < Rows.Count; index++) if (ids.Contains(Rows[index].Id)) Selection.Select(index);
    }
}
