namespace SbStudio.Core;

/// <summary>Layer-list insertion: higher rows are drawn later (in front).</summary>
public static class SpriteOrder
{
    public static bool CanMove(IReadOnlyList<SbSprite> moving, SbLayer layer, SbSprite? target, SbObjectGroup? container) =>
        moving.Count > 0 && (int)layer >= 0 && (int)layer < SbInfo.LayerCount &&
        moving.All(s => !s.Locked && s.Group == container && s.Difficulty == moving[0].Difficulty) &&
        (target == null || (!moving.Contains(target) && target.Group == container && target.Difficulty == moving[0].Difficulty));

    public static bool Move(Storyboard doc, IReadOnlyList<SbSprite> moving, SbLayer layer, SbSprite? target, bool above, SbObjectGroup? container)
    {
        if (!CanMove(moving, layer, target, container) || target != null && target.Layer != layer) return false;
        var set = moving.ToHashSet();
        var ordered = doc.Objects.OfType<SbSprite>().Where(set.Contains).ToList();
        if (ordered.Count != moving.Count || target != null && !doc.Objects.Contains(target)) return false;
        var before = doc.Objects.ToArray();
        bool changedLayer = ordered.Any(s => s.Layer != layer);
        doc.Objects.RemoveAll(o => o is SbSprite sprite && set.Contains(sprite));
        int index;
        if (target != null) index = doc.Objects.IndexOf(target) + (above ? 1 : 0);
        else
        {
            // Dropping on a section header places the objects at the front of that layer.
            index = doc.Objects.FindLastIndex(o => o is SbSprite s && s.Layer == layer && s.Group == container && s.Difficulty == moving[0].Difficulty) + 1;
            if (index == 0) index = doc.Objects.Count;
        }
        foreach (var sprite in ordered) sprite.Layer = layer;
        doc.Objects.InsertRange(index, ordered);
        return changedLayer || !before.SequenceEqual(doc.Objects);
    }
}
