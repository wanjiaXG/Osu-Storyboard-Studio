using System.Collections;
using System.Numerics;
using SbStudio.Core;

namespace SbStudio.App.Ui;

public sealed unsafe partial class ViewerPanel
{
    private sealed class FreeTarget
    {
        public required IList Nodes;
        public required CommandKind Kind;
        public required double[] Start;
        public SbSprite? Sprite;
        public SbObjectGroup? Group;
        public int Offset, End = int.MaxValue;
        public int? Previous;
        public bool Stopped;
    }

    private sealed class FreeGesture
    {
        public int StartTime, LastTime;
        public bool Started;
        public readonly List<FreeTarget> Targets = new();
    }

    private FreeGesture? freeGesture;

    private void BeginFreeGesture()
    {
        freeGesture = null;
        if (!dragging || !app.Playing || !app.Settings.FreeMode) return;
        if(app.ActiveTrack is {} track && SbInfo.IsEditorOnly(track.Kind)) return;
        int time = (int)Math.Round(app.Time);
        var gesture = new FreeGesture { StartTime = time, LastTime = time };
        if (adjusting)
        {
            if (adjustGroup != null) AddGroup(adjustGroup, app.ActiveTrack!.Value.Kind);
            // A trigger's commands count from each firing, so a gesture recorded over time has nowhere to go.
            else if (adjustSprite != null && LoopOf(adjustSprite, app.ActiveTrack!.Value.Nodes) is not SbTrigger)
                AddSprite(adjustSprite, app.ActiveTrack!.Value.Kind, LoopOf(adjustSprite, app.ActiveTrack.Value.Nodes) as SbLoop);
        }
        else
        {
            foreach (var sprite in app.Selection.OfType<SbSprite>()) AddSprite(sprite, CommandKind.M, null);
            foreach (var group in app.SelectedGroups) AddGroup(group, CommandKind.M);
        }
        // An unsupported or locked target must not fall through to moving the whole animation.
        freeGesture = gesture;

        void AddSprite(SbSprite sprite, CommandKind kind, SbLoop? loop)
        {
            if (sprite.Locked || sprite.Hidden || PluginLink.IsGenerated(sprite) || app.IsProtectedBeatmapBackground(sprite)) return;
            if (loop != null && (time < loop.AbsoluteStart || time > loop.AbsoluteEnd)) return;
            IList nodes = loop == null ? sprite.Nodes : loop.Commands;
            if (KeyTrack.Read(nodes, kind) == null) return;
            int offset = loop?.StartAt(time) ?? 0;
            gesture.Targets.Add(new FreeTarget
            {
                Sprite = sprite, Nodes = nodes, Kind = kind, Offset = offset,
                End = loop == null ? int.MaxValue : Math.Min(loop.AbsoluteEnd, offset + loop.IterationLength),
                Start = EditorApp.StateValues(kind, sprite.Timeline.Evaluate(time, sprite), sprite.Timeline.HasCommands),
            });
        }

        void AddGroup(SbObjectGroup group, CommandKind kind)
        {
            var content = app.Index.ContentOf(group);
            if (!content.Sprites.Any(s => !s.Locked && !s.Hidden) || KeyTrack.Read(group.Commands, kind) == null) return;
            gesture.Targets.Add(new FreeTarget
            {
                Group = group, Nodes = group.Commands, Kind = kind,
                Start = EditorApp.StateValues(kind, group.Timeline.Evaluate(time, group)),
            });
        }
    }

    private void RecordFreeGesture(Vector2 total, bool final = false)
    {
        if (freeGesture is not { } gesture || !dragMoved) return;
        int time = (int)Math.Round(app.Time);
        // A seek backwards ends this recording rather than overwriting the earlier gesture.
        if (time < gesture.LastTime)
        {
            foreach (var target in gesture.Targets) target.Stopped = true;
            return;
        }
        if (!final && gesture.Started && time - gesture.LastTime < 16) return;
        foreach (var target in gesture.Targets)
        {
            if (target.Stopped) continue;
            if (target.Sprite is { } owner && (!app.IsSelected(owner) || !app.Doc.Objects.Contains(owner) || owner.Locked || owner.Hidden)) continue;
            if (target.Group is { } group && !app.SelectedGroups.Contains(group)) continue;
            int at = Math.Min(time, target.End);
            if (at < gesture.StartTime) continue;
            if (!gesture.Started && !Write(target, gesture.StartTime, target.Start)) continue;
            var (dx, dy) = GroupTimeline.ToMemberOffset(target.Group != null ? target.Group.Parent : target.Sprite?.Group, app.Time,
                Math.Round(total.X), Math.Round(total.Y));
            double right = total.X, up = -total.Y;
            var start = target.Start;
            double[] value = target.Kind switch
            {
                CommandKind.M => new[] { start[0] + dx, start[1] + dy },
                CommandKind.MX => new[] { start[0] + dx },
                CommandKind.MY => new[] { start[0] + dy },
                CommandKind.F => new[] { Math.Round(Math.Clamp(start[0] + up / 200, 0, 1), 3) },
                CommandKind.S => new[] { Scaled(start[0], up) },
                CommandKind.V => new[] { Scaled(start[0], right), Scaled(start[1], up) },
                _ => new[] { Math.Round(start[0] + up / 50, 5) },
            };
            Write(target, at, value);
            if (time >= target.End) target.Stopped = true;
        }
        gesture.Started = true;
        gesture.LastTime = time;

        bool Write(FreeTarget target, int at, double[] value)
        {
            if (!app.RecordFreehandKey(target.Nodes, target.Kind, at - target.Offset, value, target.Offset, target.Previous))
            {
                target.Stopped = true;
                return false;
            }
            target.Previous = at - target.Offset;
            if (target.Group != null) app.TouchGroup(target.Group);
            else app.Touch(target.Sprite);
            return true;
        }
    }
}
