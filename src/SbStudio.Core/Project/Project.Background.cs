namespace SbStudio.Core;

public sealed partial class Project
{
    // osu!stable EventManager.Add checks the Background layer and the exact background filename.
    // Origin, position and the presence of commands do not participate in replacement detection.
    public bool IsBeatmapBackground(SbObject obj)
    {
        if (obj is not SbSprite || obj.Layer != SbLayer.Background || obj.Path.Length == 0) return false;
        if (obj.Difficulty is { } file)
            return Settings.Difficulties.Any(d => d.File == file && d.Background == obj.Path);
        return Settings.Background == obj.Path || Settings.Shared?.Background == obj.Path ||
               Settings.Difficulties.Any(d => d.Background == obj.Path);
    }

    public bool IsBeatmapBackgroundPlaceholder(SbObject obj) => obj is SbSprite { BeatmapBackgroundPlaceholder: true };

    /// <summary>Whole-script editing may change paths, but must keep placeholders as plain background declarations.</summary>
    public bool PreservesBackgroundPlaceholders(Storyboard replacement)
    {
        var protectedObjects = Storyboard.AllObjects.Where(IsBeatmapBackgroundPlaceholder).ToList();
        if (protectedObjects.Count == 0) return true;
        var options = new OsbWriteOptions { IncludeEditorMeta = true };
        string Signature(SbObject obj)
        {
            var copy = (SbSprite)obj.Clone();
            copy.Path = "background";
            copy.BeatmapBackgroundPlaceholder = true;
            return OsbWriter.WriteObjects(new[] { copy }, options);
        }
        var available = replacement.AllObjects.Where(o => o is SbSprite { IsAnimation: false, Nodes.Count: 0, Origin: SbOrigin.TopLeft, X: 0, Y: 0, Hidden: false } && o.Layer == SbLayer.Background && o.LockOverride == null)
            .GroupBy(Signature).ToDictionary(g => g.Key, g => g.Count());
        foreach (var obj in protectedObjects)
        {
            string signature = Signature(obj);
            if (!available.TryGetValue(signature, out int count) || count == 0) return false;
            available[signature] = count - 1;
        }
        return true;
    }

    internal static void NormalizeBackgroundPlaceholders(Storyboard storyboard, Func<SbObject, bool> matchesBackground)
    {
        foreach (var sprite in storyboard.AllObjects.OfType<SbSprite>())
        {
            if (!sprite.BeatmapBackgroundPlaceholder && (sprite.IsAnimation || sprite.Nodes.Count != 0 || !matchesBackground(sprite))) continue;
            sprite.BeatmapBackgroundPlaceholder = true;
            sprite.Layer = SbLayer.Background;
            sprite.Origin = SbOrigin.TopLeft;
            sprite.X = sprite.Y = 0;
            sprite.Name = null;
            sprite.Hidden = false;
            sprite.DefaultLocked = false;
            sprite.LockOverride = null;
            sprite.IsAnimation = false;
            sprite.Nodes.Clear();
            sprite.Group = null;
            sprite.Touch();
        }
    }
}
