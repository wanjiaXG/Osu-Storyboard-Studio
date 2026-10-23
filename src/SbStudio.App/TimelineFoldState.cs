using System.Collections;
using System.Runtime.CompilerServices;
using SbStudio.Core;

namespace SbStudio.App;

/// <summary>Session-only folding shared by an object and its history copies, independent of document values.</summary>
internal sealed class TimelineFoldState
{
    private sealed class Fold
    {
        internal bool Expanded;
        internal bool? CameraExpanded;
    }

    // Weak keys let discarded history and closed projects release their view state naturally.
    private readonly ConditionalWeakTable<object, Fold> folds = new();
    private sealed class ParameterFold { internal bool Expanded; }
    private readonly ConditionalWeakTable<IList, ParameterFold> parameterFolds = new();
    private ParameterFold ParametersOf(IList nodes) => parameterFolds.GetValue(nodes, _ => new());
    internal bool ParametersExpanded(IList nodes) => ParametersOf(nodes).Expanded;
    internal void ExpandParameters(IList nodes, bool expanded) => ParametersOf(nodes).Expanded = expanded;
    private void LinkParameters(IList source, IList target) => parameterFolds.Add(target, ParametersOf(source));
    private Fold Of(SbObject obj) => folds.GetValue(obj, key => new() { Expanded = ((SbObject)key).Expanded });
    private Fold Of(SbObjectGroup group) => folds.GetValue(group, key => new()
    {
        Expanded = ((SbObjectGroup)key).Expanded,
        CameraExpanded = ((SbObjectGroup)key).Camera?.Expanded,
    });

    internal void Remember(Storyboard doc)
    {
        foreach (var obj in doc.Objects.Concat(doc.Parked))
            if (!PluginLink.IsGenerated(obj)) Of(obj).Expanded = obj.Expanded;
        foreach (var group in doc.Groups)
        {
            if (PluginLink.IsGenerated(group)) continue;
            var fold = Of(group);
            fold.Expanded = group.Expanded;
            if (group.Camera != null) fold.CameraExpanded = group.Camera.Expanded;
        }
    }

    /// <summary>A transformation explicitly identifies which new object replaces this instance.</summary>
    internal void Link(SbObject source, SbObject target)
    {
        folds.Add(target, Of(source));
        target.Expanded = source.Expanded;
        if (source is SbSprite sprite && target is SbSprite copy) LinkParameters(sprite.Nodes, copy.Nodes);
    }

    internal void Link(SbObjectGroup source, SbObjectGroup target)
    {
        folds.Add(target, Of(source));
        target.Expanded = source.Expanded;
        if (source.Camera != null && target.Camera != null) target.Camera.Expanded = source.Camera.Expanded;
    }

    internal (List<SbObject> Objects, List<SbObject> Parked, List<SbObjectGroup> Groups) Clone(
        IReadOnlyList<SbObject> objects, IReadOnlyList<SbObject> parked, IReadOnlyList<SbObjectGroup> groups,
        bool skipGenerated = false, bool restore = false)
    {
        var copy = Storyboard.CloneContent(objects, parked, groups, skipGenerated);
        LinkObjects(objects, copy.Objects);
        LinkObjects(parked, copy.Parked);
        int index = 0;
        foreach (var group in groups)
        {
            if (skipGenerated && PluginLink.IsGenerated(group)) continue;
            var target = copy.Groups[index++];
            var fold = Of(group);
            folds.Add(target, fold);
            if (!restore) continue;
            target.Expanded = fold.Expanded;
            if (target.Camera != null && fold.CameraExpanded is bool expanded) target.Camera.Expanded = expanded;
        }
        return copy;

        void LinkObjects(IReadOnlyList<SbObject> source, List<SbObject> destination)
        {
            int index = 0;
            foreach (var obj in source)
            {
                if (skipGenerated && PluginLink.IsGenerated(obj)) continue;
                var target = destination[index++];
                var fold = Of(obj);
                folds.Add(target, fold);
                if (obj is SbSprite sprite && target is SbSprite targetSprite)
                {
                    LinkParameters(sprite.Nodes, targetSprite.Nodes);
                    // CloneContent preserves every node and its position within the sprite.
                    for (int n = 0; n < sprite.Nodes.Count; n++)
                        if (sprite.Nodes[n] is SbGroup block && targetSprite.Nodes[n] is SbGroup targetBlock)
                            LinkParameters(block.Commands, targetBlock.Commands);
                }
                if (restore) target.Expanded = fold.Expanded;
            }
        }
    }
}
