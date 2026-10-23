namespace SbStudio.App.Ui;

/// <summary>A read-only selection of source objects, separate from editable storyboard objects.</summary>
internal sealed class TimelineHitObjectSelection
{
    private TimelineHitObjectIndex index = TimelineHitObjectIndex.Empty;
    private readonly HashSet<int> selected = new();
    private int anchor = -1;
    public int Count => selected.Count;
    public IEnumerable<TimelineHitObject> Objects => index.Objects.Where(e => selected.Contains(e.Index));
    public bool Contains(int sourceIndex) => selected.Contains(sourceIndex);

    public void Reset(TimelineHitObjectIndex snapshot)
    {
        index = snapshot;
        Clear();
    }
    public void Clear() { selected.Clear(); anchor = -1; }
    public void Replace(IEnumerable<int> indices)
    {
        var wanted = indices.ToHashSet();
        selected.Clear();
        foreach (var entry in index.Objects)
            if (wanted.Contains(entry.Index)) selected.Add(entry.Index);
        anchor = Objects.FirstOrDefault()?.Index ?? -1;
    }
    public void SelectAll()
    {
        selected.Clear();
        foreach (var entry in index.Objects) selected.Add(entry.Index);
        anchor = index.Objects.FirstOrDefault()?.Index ?? -1;
    }
    public void Select(int sourceIndex, bool toggle = false, bool range = false)
    {
        int end = Array.FindIndex(index.Objects, e => e.Index == sourceIndex);
        if (end < 0) return;
        int start = range ? Array.FindIndex(index.Objects, e => e.Index == anchor) : -1;
        if (!toggle) selected.Clear();
        if (start >= 0)
        {
            for (int i = Math.Min(start, end); i <= Math.Max(start, end); i++) selected.Add(index.Objects[i].Index);
        }
        else
        {
            if (!toggle || !selected.Remove(sourceIndex)) selected.Add(sourceIndex);
            anchor = sourceIndex;
        }
    }
}
