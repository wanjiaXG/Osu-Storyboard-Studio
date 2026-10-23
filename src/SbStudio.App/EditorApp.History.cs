using SbStudio.Core;

namespace SbStudio.App;

/// <summary>A state in the current session, in chronological order, including states available to redo.</summary>
public sealed record HistoryEntry(int Index, string Label, bool Current, bool Future);

public sealed partial class EditorApp
{
    private enum HistoryAction
    {
        LinkPlugin, DetachPlugin, DetachPluginCopy,
        AddCamera, RemoveCamera, MoveCamera,
        Edit, Group, Ungroup, AddObjects, DeleteObjects, AddCommands, DeleteCommands,
        SwitchDifficulty, Difficulty, Asset, Rename, Visibility, Lock, Layer, Move, Reorder,
        ApplySimulationRules,
        Variables,
        AddTiming, DeleteTiming, EditTiming, CalibrateTiming, AdjustTiming, AdjustTimingOffset, AnalyseTiming,
    }

    private sealed record HistoryLabel(HistoryAction Action, string? Detail = null)
    {
        public string Text => (Action switch
        {
            HistoryAction.AddCamera => L.T("camera.add"),
            HistoryAction.RemoveCamera => L.T("camera.remove"),
            HistoryAction.MoveCamera => L.T("camera.move"),
            HistoryAction.LinkPlugin => L.T("history.linkPlugin"),
            HistoryAction.DetachPlugin => L.T("history.detachPlugin"),
            HistoryAction.DetachPluginCopy => L.T("history.detachPluginCopy"),
            HistoryAction.Group => L.T("history.group"),
            HistoryAction.Ungroup => L.T("history.ungroup"),
            HistoryAction.AddObjects => L.T("history.addObjects"),
            HistoryAction.DeleteObjects => L.T("history.deleteObjects"),
            HistoryAction.AddCommands => L.T("history.addCommands"),
            HistoryAction.DeleteCommands => L.T("history.deleteCommands"),
            HistoryAction.SwitchDifficulty => L.T("history.switchDifficulty"),
            HistoryAction.Difficulty => L.T("history.difficulty"),
            HistoryAction.Asset => L.T("history.asset"),
            HistoryAction.Rename => L.T("history.rename"),
            HistoryAction.Visibility => L.T("history.visibility"),
            HistoryAction.Lock => L.T("history.lock"),
            HistoryAction.Layer => L.T("history.layer"),
            HistoryAction.Move => L.T("history.move"),
            HistoryAction.Reorder => L.T("history.reorder"),
            HistoryAction.ApplySimulationRules => L.T("history.applySimulationRules"),
            HistoryAction.Variables => L.T("panel.variables"),
            HistoryAction.AddTiming => L.T("timing.addOffset"),
            HistoryAction.DeleteTiming => L.T("timing.deleteOffset"),
            HistoryAction.EditTiming => L.T("history.edit") + " · " + L.T("timing.list"),
            HistoryAction.CalibrateTiming => L.T("history.calibrateTiming"),
            HistoryAction.AdjustTiming => L.T("history.edit") + " · BPM",
            HistoryAction.AdjustTimingOffset => L.T("history.edit") + " · " + L.T("settings.timing.offset"),
            HistoryAction.AnalyseTiming => L.T("timing.analysis"),
            _ => L.T("history.edit"),
        }) + (Detail is { Length: > 0 } ? " · " + Detail : "");
    }

    public int HistoryRevision { get; private set; }
    public int HistoryLimit => Settings.HistoryLimit;

    public void SetHistoryLimit(int limit)
    {
        int previous = HistoryLimit;
        Settings.HistoryLimit = limit;
        if (HistoryLimit == previous) return;
        TrimHistory();
        RefreshEffectPins();
        HistoryRevision++;
        Settings.Save();
    }

    private void TrimHistory()
    {
        int excess = undo.Count + redo.Count - HistoryLimit;
        if (excess <= 0) return;
        // Keep the current state, then the nearest redo states if the undo side is exhausted.
        int removeUndo = Math.Min(excess, undo.Count);
        undo.RemoveRange(0, removeUndo);
        if (excess > removeUndo) redo.RemoveRange(0, excess - removeUndo);
    }

    public int HistoryPosition => undo.Count;
    public int HistoryCount => undo.Count + redo.Count + (committed == null ? 0 : 1);
    public bool CanNavigateHistory => Project != null && !IsLoading && !Exporting && !PreviewingEdit;
    private (int Revision, int Language) historyListedFor = (-1, -1);
    private IReadOnlyList<HistoryEntry> historyEntries = Array.Empty<HistoryEntry>();

    public IReadOnlyList<HistoryEntry> HistoryEntries
    {
        get
        {
            if (historyListedFor == (HistoryRevision, L.Generation)) return historyEntries;
            historyListedFor = (HistoryRevision, L.Generation);
            historyEntries = HistoryStates().Select((state, index) => new HistoryEntry(index,
                state.HistoryLabel?.Text ?? L.T("history.initial"), index == HistoryPosition, index > HistoryPosition)).ToArray();
            return historyEntries;
        }
    }

    private IEnumerable<Snapshot> HistoryStates()
    {
        foreach (var state in undo) yield return state;
        if (committed != null) yield return committed;
        for (int i = redo.Count - 1; i >= 0; i--) yield return redo[i];
    }

    /// <summary>Uses the same undo/redo path as the shortcuts, preserving selection and ordering behavior.</summary>
    public void GoToHistory(int index)
    {
        if (!CanNavigateHistory || index < 0 || index >= HistoryCount) return;
        var target = HistoryStates().ElementAt(index);
        CommitEdits();
        // A pending edit can discard the redo branch or evict the oldest retained state.
        int destination = HistoryStates().TakeWhile(state => !ReferenceEquals(state, target)).Count();
        if (destination >= HistoryCount) return;
        while (HistoryPosition > destination) Undo();
        while (HistoryPosition < destination) Redo();
    }

    private static HistoryLabel DescribeHistory(Snapshot? before, Snapshot after)
    {
        if (before == null) return new(HistoryAction.Edit);
        int previousObjects = before.Objects.Count + before.Parked.Count;
        int objects = after.Objects.Count + after.Parked.Count;
        static IEnumerable<SbObject> Objects(Snapshot state) => state.Objects.Concat(state.Parked);
        static string Names(IEnumerable<string> names) => string.Join(", ", names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct());
        static string ObjectDifference(Snapshot smaller, Snapshot larger)
        {
            var remaining = Objects(smaller).GroupBy(o => (o.GetType(), o.Path, o.Name)).ToDictionary(g => g.Key, g => g.Count());
            var names = new List<string>();
            foreach (var obj in Objects(larger))
            {
                var key = (obj.GetType(), obj.Path, obj.Name);
                if (remaining.TryGetValue(key, out int count) && count > 0) remaining[key] = count - 1;
                else names.Add(obj.DisplayName);
            }
            return Names(names);
        }
        string ChangedDetails()
        {
            var names = Objects(before).Zip(Objects(after))
                .Where(pair => OsbWriter.WriteObjects(new[] { pair.First }) != OsbWriter.WriteObjects(new[] { pair.Second }))
                .Select(pair => pair.Second.DisplayName).ToList();
            foreach (var pair in before.Groups.Zip(after.Groups))
                if (OsbWriter.WriteGroups(new[] { pair.First }) != OsbWriter.WriteGroups(new[] { pair.Second })) names.Add(pair.Second.Name);
            return Names(names);
        }
        if (after.Groups.Count != before.Groups.Count)
        {
            bool added = after.Groups.Count > before.Groups.Count;
            var smaller = added ? before.Groups : after.Groups;
            var larger = added ? after.Groups : before.Groups;
            var names = larger.GroupBy(g => g.Name)
                .Where(g => g.Count() > smaller.Count(old => old.Name == g.Key)).Select(g => g.Key);
            return new(added ? HistoryAction.Group : HistoryAction.Ungroup, Names(names));
        }
        if (objects != previousObjects) return objects > previousObjects
            ? new(HistoryAction.AddObjects, ObjectDifference(before, after))
            : new(HistoryAction.DeleteObjects, ObjectDifference(after, before));

        static int Commands(Snapshot state) => Objects(state).OfType<SbSprite>().Sum(s => s.Nodes.Sum(n => n is SbGroup block ? block.Commands.Count : 1))
            + state.Groups.Sum(g => g.Commands.Count);
        int previousCommands = Commands(before), commands = Commands(after);
        if (commands != previousCommands) return new(commands > previousCommands ? HistoryAction.AddCommands : HistoryAction.DeleteCommands, ChangedDetails());
        if (before.Difficulty != after.Difficulty) return new(HistoryAction.SwitchDifficulty);
        if (before.Timing != null && after.Timing != null && !before.Timing.SameValues(after.Timing)) return new(HistoryAction.EditTiming);

        foreach (var (oldObject, newObject) in Objects(before).Zip(Objects(after)))
        {
            if (oldObject.Path != newObject.Path) return new(HistoryAction.Asset, newObject.DisplayName);
            if (oldObject.Name != newObject.Name) return new(HistoryAction.Rename, newObject.DisplayName);
            if (oldObject.Hidden != newObject.Hidden) return new(HistoryAction.Visibility, newObject.DisplayName);
            if (oldObject.Locked != newObject.Locked) return new(HistoryAction.Lock, newObject.DisplayName);
            if (oldObject.Layer != newObject.Layer) return new(HistoryAction.Layer, newObject.DisplayName);
            if (oldObject.Difficulty != newObject.Difficulty) return new(HistoryAction.Difficulty, newObject.DisplayName);
            if (oldObject is SbSprite oldSprite && newObject is SbSprite newSprite && (oldSprite.X != newSprite.X || oldSprite.Y != newSprite.Y))
                return new(HistoryAction.Move, newObject.DisplayName);
        }
        return new(HistoryAction.Edit, ChangedDetails());
    }
}
