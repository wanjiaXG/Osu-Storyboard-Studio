using SbStudio.Core;

namespace SbStudio.App;

public sealed partial class EditorApp
{
    public (int Start, int End)? ClipTimeRange(SbSprite? sprite, SbObjectGroup? group)
    {
        if (Project == null) return null;
        if (sprite != null && Doc.AllObjects.Contains(sprite) && sprite.Timeline.HasCommands)
            return (sprite.Timeline.StartTime, sprite.Timeline.EndTime);
        return group != null && Doc.Groups.Contains(group) ? Ui.TimelinePanel.GroupBarSpan(group, Index.ContentOf(group)) : null;
    }

    public bool TransformClipTime(SbSprite? sprite, SbObjectGroup? group, int start, int end, bool reverse, out string? error)
    {
        error = null;
        try
        {
            if (ClipTimeRange(sprite, group) is not {} range || (sprite == null) == (group == null))
                throw new InvalidOperationException(L.T("clipTime.unavailable"));
            var timing = ClipTimeTransform.Between(range.Start, range.End, start, end, reverse);
            if (timing.Scale == 1 && timing.Offset == 0) return true;
            if (sprite != null && (PluginLink.IsGenerated(sprite) || IsProtectedBeatmapBackground(sprite)))
                throw new InvalidOperationException(L.T("status.pluginLocked"));
            if (group != null && PluginLink.IsGenerated(group))
                throw new InvalidOperationException(L.T("status.pluginLocked"));
            var groups = group == null ? [] : Doc.Groups.Where(g => g.IsWithin(group) && !PluginLink.IsGenerated(g)).ToArray();
            var sprites = sprite != null ? [sprite] : Doc.SpritesIn(group!, true).Where(s => !PluginLink.IsGenerated(s)).ToArray();
            if (sprites.Any(IsProtectedBeatmapBackground)) throw new InvalidOperationException(L.T("status.pluginLocked"));
            if (sprites.Any(s => s.Locked) || groups.Any(g => g.Plugin?.Locked == true))
                throw new InvalidOperationException(L.T("clipTime.locked"));

            var spriteEdits = sprites.Select(s => (Original: s, Copy: timing.Sprite(s))).ToArray();
            var groupEdits = groups.Select(g => (Group: g, Commands: timing.Commands(g.Commands, true),
                Camera: g.Camera == null ? null : timing.Commands(g.Camera.Commands, true))).ToArray();
            var effects = new List<(SbObjectGroup Group, PluginLink Link, PluginOutput Output)>();
            foreach (var g in groups.Where(g => g.Plugin != null))
            {
                if (g.Plugin is not { Kind: PluginKind.Effect, Effect.Supported: true })
                    throw new InvalidOperationException(L.T("effects.incompatible"));
                var link = g.Plugin.Clone();
                var span = ClipTimeRange(null, g);
                int placement = span is {} s ? Math.Min(timing.Map(s.Start), timing.Map(s.End)) : timing.Map(link.Offset);
                link.Effect!.TimeOffset = timing.Offset + timing.Scale * (link.Offset + link.Effect.TimeOffset) - placement;
                link.Effect.TimeScale *= timing.Scale;
                link.Effect.FormatVersion = 2;
                link.Offset = placement;
                if (!link.Effect.Supported) throw new InvalidOperationException(L.T("clipTime.invalid"));
                plugins.TryGetValue(g.Plugin.Instance, out var runtime);
                // Validate the complete generated result before changing any live member or history.
                var output = runtime?.Output ?? PluginOutput.FromMembers(Doc, g);
                var temporary = g.Clone(); temporary.Plugin = link;
                output.Materialize(temporary, link.Offset);
                effects.Add((g, link, output));
            }

            CommitEdits();
            foreach (var edit in spriteEdits)
            {
                edit.Original.Nodes = edit.Copy.Nodes;
                edit.Original.DepthCommands = edit.Copy.DepthCommands;
                edit.Original.FrameDelay = edit.Copy.FrameDelay;
                edit.Original.VariableSource = edit.Copy.VariableSource;
                Touch(edit.Original);
            }
            foreach (var edit in groupEdits)
            {
                edit.Group.Commands = edit.Commands;
                if (edit.Camera != null) edit.Group.Camera!.Commands = edit.Camera;
                TouchGroup(edit.Group);
            }
            foreach (var edit in effects)
            {
                edit.Group.Plugin = edit.Link;
                PutPluginOutput(edit.Group, edit.Output);
            }
            ClearKeySelection();
            ActiveTrack = null;
            CommitEdits();
            SyncPlugins();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or OverflowException)
        {
            error = ex is OverflowException ? L.T("clipTime.overflow") : ex.Message;
            return false;
        }
    }

    internal void SetEffectClipRange(SbObjectGroup group, int start, int end)
    {
        if (ClipTimeRange(null, group) is not {} span || group.Plugin?.Effect is not {} effect || end <= start) return;
        var timing = ClipTimeTransform.Between(span.Start, span.End, start, end);
        var link = group.Plugin.Clone();
        link.Effect!.TimeOffset = timing.Offset + timing.Scale * (link.Offset + effect.TimeOffset) - start;
        link.Effect.TimeScale *= timing.Scale;
        link.Effect.FormatVersion = 2;
        link.Offset = start;
        plugins.TryGetValue(group.Plugin.Instance, out var runtime);
        if (runtime?.Output is {} output)
        {
            var temporary = group.Clone(); temporary.Plugin = link;
            output.Materialize(temporary, start);
        }
        BeginEffectEdit(group);
        group.Plugin = link;
        if (runtime?.Output != null) PutPluginOutput(group, runtime.Output);
        TouchGroup(group);
        SyncPlugins();
    }
}
