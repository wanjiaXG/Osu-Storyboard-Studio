using System.Collections;
using SbStudio.Core;

namespace SbStudio.App;

public sealed partial class EditorApp
{
    internal bool ParametersExpanded(IList nodes) => timelineFolds.ParametersExpanded(nodes);

    internal void ExpandParameters(IList nodes, bool expanded)
    {
        timelineFolds.ExpandParameters(nodes, expanded);
        TouchFolds();
    }

    /// <summary>Adds one independent flag at the playhead, relative to its current loop pass or firing.</summary>
    internal bool AddParameter(SbSprite sprite, SbGroup? block, char parameter)
    {
        if (Project == null || Playing || !Doc.Objects.Contains(sprite) || IsProtectedBeatmapBackground(sprite) ||
            PluginLink.IsGenerated(sprite) || block != null && !sprite.Nodes.Contains(block) || !"HVA".Contains(parameter)) return false;
        IList nodes = block == null ? sprite.Nodes : block.Commands;
        int snapped = SnappedTime;
        if (block is SbLoop loop && Time >= loop.AbsoluteStart && Time <= loop.AbsoluteEnd)
            snapped = Math.Clamp(snapped, loop.AbsoluteStart, loop.AbsoluteEnd);
        int offset = block is SbLoop pass ? pass.StartAt(snapped) : BlockOffset(block);
        long start = Math.Max(block == null ? int.MinValue : 0L, (long)snapped - offset);
        long absolute = offset + start;
        if (absolute < int.MinValue || absolute > int.MaxValue) return false;
        long end = (long)AfterBeats((int)absolute, 2) - offset;
        if (block is SbLoop bounded && start < bounded.IterationLength)
            end = Math.Min(end, bounded.IterationLength);
        if (start > int.MaxValue || end > int.MaxValue || end < start) return false;
        CommitEdits();
        var (command, added) = ParameterTrack.Add(nodes, parameter, (int)start, (int)end);
        SelectOnly(sprite);
        SelectTrack(nodes, CommandKind.P, parameter);
        SelectedCommands.Add(command);
        ExpandParameters(nodes, true);
        if (added) { Touch(sprite); CommitEdits(); }
        return added;
    }
}
