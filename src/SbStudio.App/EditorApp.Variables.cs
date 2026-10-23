using SbStudio.Core;

namespace SbStudio.App;

public sealed partial class EditorApp
{
    public string? ChangeVariable(StoryboardVariable? old, StoryboardVariable? value)
    {
        if (!CanNavigateHistory) return L.T("variables.unavailable");
        if (old != null && !Doc.Variables.Contains(old)) return L.T("variables.stale");
        if (value != null)
        {
            if (!value.Name.StartsWith('$') || !StoryboardVariables.ValidName(value.Name[1..])) return L.T("variables.invalidName");
            if (!StoryboardVariables.ValidValue(value.Value)) return L.T("variables.invalidValue");
            if (Doc.Variables.Any(v => v != old && v.Name == value.Name && v.Difficulty == value.Difficulty)) return L.T("variables.duplicate");
        }
        if (old == value) return null;
        var parsed = VariableEditing.Apply(Doc, old, value);
        if (parsed.HasErrors) return L.T("variables.invalidResult", parsed.Diagnostics.First(d => d.IsError).Message);
        if (!Project!.PreservesBackgroundPlaceholders(parsed.Storyboard)) return L.T("props.beatmapBg.pathOnly");
        if (AssetPathPolicy.InvalidPath(Project, parsed.Storyboard) is { } path) return AssetPathPolicy.Message(path);
        CommitEdits();
        timelineFolds.Remember(Doc);
        var previousObjects = Doc.AllObjects.ToDictionary(o => o.Id);
        foreach (var (target, id) in parsed.ObjectKeys)
            if (previousObjects.TryGetValue(id, out var source) && source.GetType() == target.GetType()) timelineFolds.Link(source, target);
        var previousGroups = Doc.Groups.ToDictionary(g => g.Id);
        foreach (var (target, id) in parsed.GroupKeys)
            if (previousGroups.TryGetValue(id, out var source)) timelineFolds.Link(source, target);
        ReplaceAll(parsed.Storyboard);
        RefreshPlayback();
        CommitEdits();
        if (committed != null) committed = committed with { HistoryLabel = new(HistoryAction.Variables) };
        HistoryRevision++;
        return null;
    }
}
