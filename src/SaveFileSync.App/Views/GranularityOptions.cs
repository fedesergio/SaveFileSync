using SaveFileSync.Core.Model;

namespace SaveFileSync.App.Views;

/// <summary>Texts for the "conflict detection" choice (PLAN.md §6.4).</summary>
public static class GranularityOptions
{
    public static readonly IReadOnlyList<(MergeGranularity Value, string Text)> All =
    [
        (MergeGranularity.Unit, "Whole game — a change made on two PCs is always a conflict"),
        (MergeGranularity.Folder, "Per save folder — different saves merge automatically (e.g. RPCS3 savedata)"),
        (MergeGranularity.File, "Per file — independent save slots merge automatically"),
    ];

    public const string Hint =
        "Choose 'Whole game' when the files depend on each other (e.g. Ryujinx: save folders plus an index). " +
        "With 'Per save folder' each first-level folder inside the save folder is handled on its own.";

    public static IReadOnlyList<string> Texts => All.Select(o => o.Text).ToList();

    public static int IndexOf(MergeGranularity value) => Math.Max(0, All.ToList().FindIndex(o => o.Value == value));

    public static MergeGranularity At(int index) => index >= 0 && index < All.Count ? All[index].Value : MergeGranularity.Unit;
}
