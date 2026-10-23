using System.Collections;
using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using SbStudio.Core;

namespace SbStudio.App.Ui;

/// <summary>Effect-controls style editor for the selected object and its commands.</summary>
public sealed partial class PropertiesPanel
{
    private static readonly string[] EasingLabels = SbInfo.EasingNames.Select((n, i) => $"{i}  {n}").ToArray();
    private static readonly string[] OriginNames = Enum.GetNames<SbOrigin>();
    private static readonly Localized<string[]> LoopTypeNames = new(() => new[] { L.T("looptype.forever"), L.T("looptype.once") });
    private static readonly Localized<string[]> ParamNames = new(() => new[] { L.T("param.H"), L.T("param.V"), L.T("param.A") });
    private static readonly Localized<string> WindowTitle = new(() => L.T("panel.properties") + "###properties");
    /// <summary>What a keyframe can be told about getting to the next one: nothing at all, or one of the easings.</summary>
    private static readonly Localized<string[]> TransitionLabels = new(() => EasingLabels.Prepend(L.T("props.key.hold")).ToArray());
    private static readonly Localized<string[]> LastKeyLabel = new(() => new[] { L.T("props.key.last") });
    /// <summary>
    /// The same lists for the commands of a group of sprites, which can have a transition drawn by hand in
    /// place of an easing (<see cref="EasingCurve"/>): that is their last entry.
    /// </summary>
    private static readonly Localized<string[]> CurveTransitionLabels = new(() => TransitionLabels.Value.Append(L.T("props.key.custom")).ToArray());
    private static readonly Localized<string[]> CurveEasingLabels = new(() => EasingLabels.Append(L.T("props.key.custom")).ToArray());
    private readonly EditorApp app;
    private readonly ColourPickerPopup colourPicker;
    private int batchShift = 1000;
    private string assetFilter = "";
    /// <summary>Totals shown while nothing is selected, and the storyboard revision they were counted for.</summary>
    private (int Sprites, int Animations, int Samples, int Videos, int Commands, int Files) summary;
    private int summaryRevision = -1;

    public bool Open = true;
    public event Action? CommandAdded;

    public PropertiesPanel(EditorApp app) : this(app, new ColourPickerPopup(app)) { }

    internal PropertiesPanel(EditorApp app, ColourPickerPopup picker)
    {
        this.app = app;
        colourPicker = picker;
    }

    public void Draw()
    {
        colourPicker.Tick();
        if (!Open)
        {
            colourPicker.Cancel(this);
            return;
        }
        if (!ImGui.Begin(WindowTitle.Value, ref Open) || app.Project == null)
        {
            colourPicker.Cancel(this);
            ImGui.End();
            return;
        }

        firstSelected = null;
        if (app.EmptyBeatmapBackgroundSelected || app.SelectedTimelineSection == TimelineSection.BeatmapBackground)
        {
            DrawBeatmapBackground(app.EmptyBeatmapBackgroundSelected ? null : app.Doc.Sprites.FirstOrDefault(app.Project.IsBeatmapBackgroundPlaceholder));
            shownCommand = null;
            ImGui.End();
            return;
        }
        if (app.CameraSelection is {} cameraGroup)
        {
            DrawCamera(cameraGroup); ImGui.End(); return;
        }
        if (app.SelectedGroup is { } selectedGroup)
        {
            DrawObjectGroup(selectedGroup);
            if (firstSelected == null) shownCommand = null;
            ImGui.End();
            return;
        }
        if (app.SelectedGroups.Count > 0)
        {
            // Several things with a group among them: what can be done with them together.
            ImGui.SeparatorText(L.T("props.selectedThings", app.Selection.Count, app.SelectedGroups.Count));
            if (ImGui.Button(L.T("props.groupButton"))) app.GroupSelection();
            Theme.Tooltip(L.T("props.groupButton.tip"));
            Theme.TextDimmed(L.T("props.selectedThings.note"));
            shownCommand = null;
            ImGui.End();
            return;
        }

        if (app.Selection.Count > 1) DrawBatch();

        switch (app.Primary)
        {
            case SbSprite sprite: DrawSprite(sprite); break;
            case SbSample sample: DrawSample(sample); break;
            case SbVideo video: DrawVideo(video); break;
            default: DrawSummary(); break;
        }
        if (firstSelected == null) shownCommand = null;
        ImGui.End();
    }

    private void DrawSummary()
    {
        var doc = app.Doc;
        if (app.SelectedTimelineSection is { } section) ImGui.SeparatorText(Theme.SectionLabel(section));
        Theme.TextDimmed(L.T("props.noSelection"));
        ImGui.Spacing();
        ImGui.SeparatorText(L.T("props.storyboard"));
        if (summaryRevision != app.DocRevision)
        {
            // Counting walks every command of every object: once per edit, not once per frame.
            summaryRevision = app.DocRevision;
            int sprites = 0, animations = 0, samples = 0, videos = 0, commands = 0;
            foreach (var obj in doc.Objects)
            {
                if (obj is SbSprite s)
                {
                    if (s.IsAnimation) animations++;
                    else sprites++;
                    commands += s.AllCommands().Count();
                }
                else if (obj is SbVideo) videos++;
                else samples++;
            }
            summary = (sprites, animations, samples, videos, commands, AssetUsage.ReferencedFiles(doc).Count);
        }
        ImGui.TextUnformatted(L.T("props.counts1", summary.Sprites, summary.Animations, summary.Samples, summary.Videos));
        ImGui.TextUnformatted(L.T("props.counts2", summary.Commands, doc.Groups.Count));
        if (app.Index.TimeRange is { } range)
            ImGui.TextUnformatted(L.T("props.timeRange", Theme.FormatTime(range.Start), Theme.FormatTime(range.End)));
        ImGui.TextUnformatted(L.T("props.referenced", summary.Files));
    }

    private void DrawBatch()
    {
        ImGui.SeparatorText(L.T("props.selectedCount", app.Selection.Count));
        ImGui.SetNextItemWidth(Theme.Px(110));
        ImGui.InputInt("ms##shift", ref batchShift, 100);
        ImGui.SameLine();
        if (ImGui.Button(L.T("props.shiftTime")))
        {
            foreach (var obj in app.Selection.Where(o => !app.IsProtectedBeatmapBackground(o))) obj.ShiftTime(batchShift);
            app.Touch();
        }

        ImGui.SetNextItemWidth(Theme.Px(170));
        int layer = -1;
        if (ImGui.Combo(L.T("props.moveToLayer"), ref layer, Theme.LayerNames, Theme.LayerNames.Length) && layer >= 0)
        {
            foreach (var obj in app.Selection.Where(o => !app.IsProtectedBeatmapBackground(o))) obj.Layer = (SbLayer)layer;
            app.Touch();
        }
        if (ImGui.Button(L.T("props.groupButton"))) app.GroupSelection();
        Theme.Tooltip(L.T("props.groupButton.tip"));
        ImGui.Spacing();
        ImGui.SeparatorText(L.T("props.primary"));
    }

    // ---------------------------------------------------------------- showing only what is picked

    /// <summary>What the panel showed of the selected object when it was last drawn; it starts from its top when that changes.</summary>
    private (object? Owner, SbGroup? Block, CommandKind? Kind) shownPart;
    /// <summary>The keyframe that was last brought into view because it was selected.</summary>
    private KeyRef? shownKey;
    /// <summary>
    /// The same for commands that are shown as they are written: the first selected one of the panel, as
    /// it was last brought into view, and the first one met so far while the panel is drawn.
    /// </summary>
    private SbCommand? shownCommand, firstSelected;

    private static bool HasKind(IList nodes, CommandKind kind)
    {
        foreach (object? node in nodes)
            if (node is SbCommand c && c.Kind == kind) return true;
        return false;
    }

    /// <summary>
    /// What of a sprite the panel shows while it shows only what is picked: a kind of command for the
    /// keyframes of that property, a loop or trigger for all that is in it, both for the keyframes of a
    /// property of that loop or trigger, and neither for the whole sprite. It follows what is picked in the
    /// timeline: a property row, a loop or trigger, or the sprite itself.
    /// </summary>
    private (SbGroup? Block, CommandKind? Kind) Picked(SbSprite sprite, bool respectFilter = true)
    {
        if (respectFilter && !app.Settings.FilterCommands) return (null, null);
        if (app.ActiveTrack is { } track)
        {
            // A property that has lost its last command has no row to be picked any more.
            if ((track.Nodes == sprite.Nodes || track.Nodes == sprite.DepthCommands)) return (null, (HasKind(track.Nodes, track.Kind) || track.Parameter != '\0') ? track.Kind : null);
            foreach (var node in sprite.Nodes)
                if (node is SbGroup block && block.Commands == track.Nodes) return (block, (HasKind(block.Commands, track.Kind) || track.Parameter != '\0') ? track.Kind : null);
        }
        if (app.SelectedBlock is { } selected && sprite.Nodes.Contains(selected)) return (selected, null);
        return (null, null);
    }

    private static string BlockLabel(SbGroup block) => block switch
    {
        SbLoop loop => L.T("timeline.loopRow", loop.Iterations, loop.IterationLength),
        SbTrigger trigger => L.T("timeline.triggerRow", trigger.Name),
        _ => "",
    };

    /// <summary>
    /// The switch for showing only what is picked, at the head of the panel. While only a part is shown it
    /// says which, with a button that goes back to the whole object.
    /// </summary>
    /// <param name="owner">The sprite or group of sprites the panel is about.</param>
    private void DrawFilter(object owner, SbGroup? block, CommandKind? kind)
    {
        bool filter = app.Settings.FilterCommands;
        if (ImGui.Checkbox(L.T("props.filter"), ref filter))
        {
            app.Settings.FilterCommands = filter;
            app.Settings.Save();
        }
        Theme.Tooltip(L.T("props.filter.tip"));

        // Another part than last time is read from its top.
        if (shownPart != (owner, block, kind))
        {
            shownPart = (owner, block, kind);
            ImGui.SetScrollY(0);
        }
        if (block == null && kind == null) return;

        string all = L.T("props.filter.all");
        ImGui.SameLine(MathF.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - SmallButtonWidth(all)));
        if (ImGui.SmallButton(all))
        {
            app.ClearKeySelection();
            app.ActiveTrack = null;
        }
        Theme.Tooltip(L.T("props.filter.all.tip"));

        string part = kind is { } only ? Theme.KindLabel(only).Replace("  ", " ") : "";
        if (block != null) part = part.Length > 0 ? BlockLabel(block).Replace("  ", " ") + "  ›  " + part : BlockLabel(block).Replace("  ", " ");
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Accent);
        Theme.TextWrapped(L.T("props.filter.showing", part));
        ImGui.PopStyleColor();
        ImGui.Separator();
    }

    /// <summary>Whether rotation is shown in degrees: the switch that goes with the commands that have one.</summary>
    private void DegreesToggle()
    {
        bool degrees = app.Settings.RotationInDegrees;
        NextInRow(ImGui.GetStyle().ItemSpacing.X, ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(L.T("props.degrees")).X);
        if (ImGui.Checkbox(L.T("props.degrees"), ref degrees))
        {
            app.Settings.RotationInDegrees = degrees;
            app.Settings.Save();
        }
        Theme.Tooltip(L.T("props.degrees.tip"));
    }

    // ---------------------------------------------------------------- numeric fields

    // Built on use: the name is what a rejected input is reported under, in the language of the moment.
    private static NumberSpec PositionSpec => new(L.T("value.position"), 1f, "%.1f");
    private static NumberSpec OpacitySpec => new(L.T("value.opacity"), 0.005f, "%.3f", 0, 1);
    private static NumberSpec ScaleSpec => new(L.T("value.scale"), 0.005f, "%.3f");
    private static NumberSpec DegreesSpec => new(L.T("value.rotation"), 0.5f, "%.1f°", Suffix: "°");
    private static NumberSpec RadiansSpec => new(L.T("value.rotation"), 0.01f, "%.4f rad", Suffix: "rad");
    private static NumberSpec ColourSpec => new(L.T("value.colour"), 1f, "%.0f", 0, 255, true);
    internal static NumberSpec KeyValueSpec(CommandKind kind, bool degrees) => kind switch
    {
        CommandKind.F => OpacitySpec,
        CommandKind.S or CommandKind.V => ScaleSpec,
        CommandKind.C => ColourSpec,
        CommandKind.CD or CommandKind.CF => PositionSpec with {Min=0.01},
        CommandKind.R or CommandKind.RX or CommandKind.RY or CommandKind.CO or CommandKind.CI or CommandKind.CL => degrees ? DegreesSpec : RadiansSpec,
        _ => PositionSpec,
    };

    /// <summary>A number field with its label on the right, sized like ImGui's own labelled widgets.</summary>
    private bool LabeledNumber(string label, ref double value, in NumberSpec spec, string? tooltip = null)
    {
        bool changed = NumberInput.Field(app, "##" + label, ref value, ImGui.CalcItemWidth(), spec with { Name = label });
        if (tooltip != null) Theme.Tooltip(tooltip);
        ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.TextUnformatted(label);
        return changed;
    }

    private bool LabeledPair(string label, ref double x, ref double y, in NumberSpec spec, string? tooltip = null)
    {
        ImGui.BeginGroup();
        bool changed = NumberInput.Pair(app, "##" + label, ref x, ref y, ImGui.CalcItemWidth(), spec with { Name = label });
        ImGui.EndGroup();
        if (tooltip != null) Theme.Tooltip(tooltip);
        ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.TextUnformatted(label);
        return changed;
    }

    // ---------------------------------------------------------------- group of sprites

    /// <summary>
    /// The selected group's precision and the number of commands its members will export with.
    /// </summary>
    private void DrawBake(SbObjectGroup group, IReadOnlyList<SbSprite> members)
    {
        ImGui.SeparatorText(L.T("props.bake"));

        float spacing = ImGui.GetStyle().ItemInnerSpacing.X;
        float each = MathF.Max(40, (ImGui.GetContentRegionAvail().X - spacing) / 2);
        int step = group.BakeStep;
        var stepSpec = new NumberSpec(L.T("props.bake.step"), 0.2f, "%d ms", BakeOptions.LeastStep, Suffix: "ms", Caption: L.T("props.bake.step"));
        if (NumberInput.Field(app, "##bakeStep", ref step, each, stepSpec))
        {
            group.BakeStep = step;
            app.TouchGroup(group);
        }
        Theme.Tooltip(L.T("props.bake.step.tip"));
        ImGui.SameLine(0, spacing);
        double tolerance = group.BakeTolerance;
        var toleranceSpec = new NumberSpec(L.T("props.bake.tolerance"), 0.01f, "%.2f px", BakeOptions.LeastTolerance, Suffix: "px", Caption: L.T("props.bake.tolerance"));
        if (NumberInput.Field(app, "##bakeTolerance", ref tolerance, each, toleranceSpec))
        {
            group.BakeTolerance = tolerance;
            app.TouchGroup(group);
        }
        Theme.Tooltip(L.T("props.bake.tolerance.tip"));

        var effective = group.Bake;
        if (effective.MinStep != group.BakeStep || effective.PositionTolerance != group.BakeTolerance)
            Theme.TextWrapped(L.T("props.bake.nested", effective.MinStep, effective.PositionTolerance));

        // For a large group that takes a while to work out: a few milliseconds of it in every frame,
        // and the number once it is whole.
        long until = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 4 / 1000;
        int commands = 0, done = 0;
        foreach (var member in members)
        {
            if (!member.IsExportedFresh() && Stopwatch.GetTimestamp() > until) continue;
            commands += member.ExportedCommands();
            done++;
        }
        Theme.TextDimmed(done < members.Count ? L.T("props.bake.counting", done, members.Count) : L.T("props.bake.count", members.Count, commands));

        ImGui.BeginDisabled(group.BakeStep == BakeOptions.DefaultMinStep && group.BakeTolerance == BakeOptions.DefaultTolerance);
        if (ImGui.SmallButton(L.T("props.bake.reset", BakeOptions.DefaultMinStep, BakeOptions.DefaultTolerance)))
        {
            group.BakeStep = BakeOptions.DefaultMinStep;
            group.BakeTolerance = BakeOptions.DefaultTolerance;
            app.TouchGroup(group);
        }
        ImGui.EndDisabled();
    }

    private void DrawObjectGroup(SbObjectGroup group)
    {
        if(group.Plugin!=null) DrawPlugin(group);
        bool changed = false;
        CommandKind? only = app.Settings.FilterCommands && app.ActiveTrack is { } picked && picked.Nodes == group.Commands && HasKind(group.Commands, picked.Kind)
            ? picked.Kind
            : null;
        DrawFilter(group, null, only);
        if (only is { } shown)
        {
            Theme.TextDimmed(L.T("props.addKey"));
            ImGui.SameLine(0, Theme.Px(4));
            if (ImGui.SmallButton(shown.ToString()))
            {
                AddGroupKey(group, shown);
                changed = true;
            }
            Theme.Tooltip(Theme.KindLabel(shown) + "\n" + L.T("props.addKey.tip"));
            if (SbInfo.IsAngle(shown)) DegreesToggle();
            changed |= DrawTracks(group.Commands, 0, true, null, shown, true);
            if (changed) app.TouchGroup(group);
            return;
        }

        ImGui.PushItemWidth(-Theme.LabelWidth(78, L.T("props.name"), L.T("value.position"), L.T("props.pivot")));
        ImGui.SeparatorText(L.T("props.group"));

        if (ImGui.InputText(L.T("props.name"), ref group.Name, 96)) changed = true;

        double x = group.X, y = group.Y;
        if (LabeledPair(L.T("value.position"), ref x, ref y, PositionSpec,
                L.T("props.groupPosition.tip")))
        {
            group.Offset(Math.Round(x - group.X, 2), Math.Round(y - group.Y, 2));
            changed = true;
        }

        double pivotX = group.PivotX, pivotY = group.PivotY;
        if (LabeledPair(L.T("props.pivot"), ref pivotX, ref pivotY, PositionSpec,
                L.T("props.pivot.tip")))
        {
            // The pivot moves under the members: they stay where they are while nothing rotates or scales.
            double dx = Math.Round(pivotX - group.PivotX, 2), dy = Math.Round(pivotY - group.PivotY, 2);
            group.PivotX += dx;
            group.PivotY += dy;
            group.Offset(dx, dy);
            changed = true;
        }
        ImGui.PopItemWidth();

        var state = group.Timeline.Evaluate(app.Time, group);
        double rotation = app.Settings.RotationInDegrees ? state.Rotation * 180 / Math.PI : state.Rotation;
        Theme.TextDimmed(L.T("props.groupState", state.X, state.Y, state.Scale, rotation, app.Settings.RotationInDegrees ? "°" : " rad"));

        if(GroupSpace.UsesDepth(group)) Theme.TextDimmed(L.T("space.groupState",state.Z,state.RX*(app.Settings.RotationInDegrees?180/Math.PI:1),state.RY*(app.Settings.RotationInDegrees?180/Math.PI:1)));
        if (group.Plugin==null && ImGui.Button(L.T("group.enter"))) app.OpenContainer(group);
        Theme.Tooltip(L.T("group.enter.tip"));
        if (app.SelectedGroup != group) return;

        // What is in it: the groups, then the sprites. A click goes into the group and selects that one there.
        var members = app.Index.MembersOf(group);
        var inner = app.Doc.ChildrenOf(group).ToList();
        ImGui.SetNextItemOpen(true, ImGuiCond.Once);
        if (ImGui.CollapsingHeader(L.T("props.members", members.Count + inner.Count) + "###members") && group.Plugin==null)
        {
            float button = MathF.Max(Theme.Px(46), ImGui.CalcTextSize(L.T("props.removeMember")).X + ImGui.GetStyle().FramePadding.X * 2);
            foreach (var child in inner)
            {
                ImGui.PushID(child.Id);
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.GroupColor);
                bool chosen = ImGui.Selectable(child.Name.Length > 0 ? child.Name : L.T("group.unnamed"), false, ImGuiSelectableFlags.None,
                    new Vector2(ImGui.GetContentRegionAvail().X - button - 8, 0));
                ImGui.PopStyleColor();
                Theme.Tooltip(L.T("props.member.tip"));
                ImGui.SameLine();
                bool out_ = ImGui.SmallButton(L.T("props.removeMember"));
                ImGui.PopID();
                if (chosen)
                {
                    app.OpenContainer(group);
                    app.SelectGroup(child);
                }
                else if (out_) app.MoveOut(child);
            }
            foreach (var member in members)
            {
                ImGui.PushID(member.Id);
                bool chosen = ImGui.Selectable(member.DisplayName, false, ImGuiSelectableFlags.None, new Vector2(ImGui.GetContentRegionAvail().X - button - 8, 0));
                Theme.Tooltip(L.T("props.member.tip"));
                ImGui.SameLine();
                bool out_ = ImGui.SmallButton(L.T("props.removeMember"));
                ImGui.PopID();
                if (chosen)
                {
                    app.OpenContainer(group);
                    app.SelectOnly(member);
                }
                // Out to where the group stands: the group it is in, or the storyboard.
                else if (out_) app.SetGroup(member, group.Parent);
            }
            Theme.TextDimmed(L.T("props.addMemberNote"));
        }
        // Removing the last member dissolves the group, and a click on one goes into it.
        if (app.SelectedGroup != group) return;

        if (group.Plugin==null && ImGui.Button(L.T("group.ungroupKeep"))) app.Ungroup(group, true);
        Theme.Tooltip(L.T("group.ungroupKeep.tip"));
        ImGui.SameLine();
        if (group.Plugin==null && ImGui.Button(L.T("group.ungroupDiscardShort"))) app.Ungroup(group, false);
        Theme.Tooltip(L.T("group.ungroupDiscard.tip"));
        if (app.SelectedGroup != group) return;

        DrawBake(group, members);
        DrawSpace(group);

        ImGui.SeparatorText(L.T("props.groupCommands"));
        changed |= DrawGroupAddButtons(group);
        bool degrees = app.Settings.RotationInDegrees;
        NextInRow(Theme.Px(14), ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(L.T("props.degrees")).X);
        if (ImGui.Checkbox(L.T("props.degrees"), ref degrees))
        {
            app.Settings.RotationInDegrees = degrees;
            app.Settings.Save();
        }

        if (group.Commands.Count == 0)
            Theme.TextDimmed(L.T("props.groupEmpty"));

        changed |= DrawTracks(group.Commands, 0, true, null, null, true);
        if (changed) app.TouchGroup(group);
    }

    private bool DrawGroupAddButtons(SbObjectGroup group)
    {
        bool added = false;
        Theme.TextDimmed(L.T("props.addKey"));
        foreach (var kind in SbInfo.AllKinds.Concat(SbInfo.DepthKinds).Concat(SbInfo.CameraKinds))
        {
            if (!SbObjectGroup.Accepts(kind)) continue;
            NextInRow(Theme.Px(4), SmallButtonWidth(kind.ToString()));
            if (ImGui.SmallButton(kind.ToString()))
            {
                added |= AddGroupKey(group, kind);
            }
            Theme.Tooltip(Theme.KindLabel(kind) + "\n" + (kind==CommandKind.Z ? L.T("space.z.tip") : kind is CommandKind.RX or CommandKind.RY ? L.T("space.rotation.tip") : L.T("props.addKey.tip")));
        }
        if (added) CommandAdded?.Invoke();
        return added;
    }

    /// <summary>Adds a keyframe at the playhead that holds what the group's property is at that moment.</summary>
    private bool AddGroupKey(SbObjectGroup group, CommandKind kind)
    {
        var result = app.AddKey(group, kind);
        if (result != EditorApp.KeyResult.NotKeyframes) return result == EditorApp.KeyResult.Done;
        // Commands that run at the same time are no keyframes; one more of them is all that can be added.
        int time = app.SnappedTime;
        var command = new SbCommand(kind, 0, time, app.AfterBeats(time, 2), EditorApp.StateValues(kind, group.Timeline.Evaluate(time, group)));
        if (app.RefusesCommand(group.Commands, command)) return false;
        group.Commands.Add(command);
        group.Expanded = true;
        app.SelectTrack(group.Commands, kind);
        app.SelectedCommands.Add(command);
        return true;
    }

    // ---------------------------------------------------------------- sprite

    private void DrawSprite(SbSprite sprite)
    {
        if (app.Project!.IsBeatmapBackgroundPlaceholder(sprite))
        {
            DrawBeatmapBackground(sprite);
            return;
        }
        ImGui.BeginDisabled(app.IsProtectedBeatmapBackground(sprite));
        DrawSpriteBody(sprite);
        ImGui.EndDisabled();
    }

    private void DrawBeatmapBackground(SbSprite? sprite)
    {
        string path = sprite?.Path ?? "";
        ImGui.SeparatorText(L.T("props.beatmapBg"));
        ImGui.PushItemWidth(-1);
        if (AssetCombo("##beatmapBackgroundPath", ref path, AssetKind.Image, dropdownStyle: true, allowEmpty: true))
        {
            if (path.Length == 0)
            {
                if (sprite != null) app.ClearBeatmapBackground(sprite);
            }
            else if (sprite == null) app.AddBeatmapBackground(path);
            else { sprite.Path = path; app.Touch(sprite); }
        }
        Theme.Tooltip(L.T("props.beatmapBg.path") + ": " + path);
        ImGui.PopItemWidth();
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
        Theme.TextWrapped(L.T("props.beatmapBg.description"));
        ImGui.PopStyleColor();
        if (ImGui.SmallButton(L.T("props.beatmapBg.help")))
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://osu.ppy.sh/wiki/Client/Beatmap_editor/SB_load") { UseShellExecute = true });
            }
            catch (Exception ex) { app.ShowMessage(L.T("msg.openFailed"), ex.Message); }
        }
    }

    private void DrawSpriteBody(SbSprite sprite)
    {
        var (block, kind) = Picked(sprite);
        DrawFilter(sprite, block, kind);
        if (block == null && kind == null)
        {
            DrawWholeSprite(sprite);
            return;
        }

        bool changed = false;
        if (kind is { } only)
        {
            // The keyframes of one property, of the sprite itself or of one of its loops and triggers.
            changed |= DrawAddButtons(sprite, block, only);
            if (SbInfo.IsAngle(only)) DegreesToggle();
            if (block is SbTrigger shownTrigger) DrawTriggerTimeNote(sprite, shownTrigger);
            keyTimeFormat = block is SbTrigger ? "+%d ms" : "%d";
            changed |= block == null
                ? DrawTracks(SbInfo.DepthKinds.Contains(only) ? sprite.DepthCommands : sprite.Nodes, 0, true, null, only, SbInfo.DepthKinds.Contains(only))
                : DrawTracks(block.Commands, GroupStart(block), true, app.BlockOffset(block), only);
            keyTimeFormat = "%d";
        }
        else if (block != null)
        {
            // A loop or trigger with all that is in it.
            ImGui.PushID(sprite.Nodes.IndexOf(block));
            bool remove = block is SbLoop loop ? DrawLoop(sprite, loop, ref changed) : block is SbTrigger trigger && DrawTrigger(sprite, trigger, ref changed);
            ImGui.PopID();
            if (remove)
            {
                sprite.Nodes.Remove(block);
                app.ClearKeySelection();
                changed = true;
            }
        }
        if (changed) app.Touch(sprite);
    }

    private void DrawWholeSprite(SbSprite sprite)
    {
        bool changed = false;
        float labelWidth = Theme.LabelWidth(78, L.T("props.name"), L.T("props.file"), L.T("props.layer"), L.T("props.origin"), L.T("props.group"),
            L.T("props.initialPosition"), L.T("props.frameCount"), L.T("props.frameDelay"), L.T("props.loopType"), L.T("props.owner"));
        ImGui.PushItemWidth(-labelWidth);

        string name = sprite.Name ?? "";
        if (ImGui.InputTextWithHint(L.T("props.name"), sprite.DisplayName, ref name, 128))
        {
            sprite.Name = name.Length == 0 ? null : name;
            changed = true;
        }
        Theme.Tooltip(L.T("props.name.tip"));

        changed |= AssetCombo(L.T("props.file"), ref sprite.Path, AssetKind.Image);
        if (app.Textures.IsUnavailable(sprite.IsAnimation ? SbSprite.FramePath(sprite.Path, 0) : sprite.Path))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError);
            Theme.TextWrapped(sprite.IsAnimation
                ? L.T("props.firstFrameMissing", SbSprite.FramePath(sprite.Path, 0))
                : L.T("props.fileMissing"));
            ImGui.PopStyleColor();
        }

        int layer = (int)sprite.Layer;
        if (ImGui.Combo(L.T("props.layer"), ref layer, Theme.LayerNames, Theme.LayerNames.Length))
        {
            sprite.Layer = (SbLayer)layer;
            changed = true;
        }

        int origin = (int)sprite.Origin;
        if (ImGui.Combo(L.T("props.origin"), ref origin, OriginNames, OriginNames.Length))
        {
            sprite.Origin = (SbOrigin)origin;
            changed = true;
        }

        if (app.Doc.Groups.Count > 0)
        {
            if (ImGui.BeginCombo(L.T("props.group"), sprite.Group?.Name ?? L.T("common.none")))
            {
                if (ImGui.Selectable(L.T("common.none"), sprite.Group == null)) app.SetGroup(sprite, null);
                foreach (var candidate in app.Doc.Groups.ToArray())
                {
                    ImGui.PushID(candidate.Id);
                    // A group in a group is set in under it.
                    string indent = new string(' ', candidate.Depth * 3);
                    if (ImGui.Selectable(indent + (candidate.Name.Length > 0 ? candidate.Name : L.T("group.unnamed")), sprite.Group == candidate)) app.SetGroup(sprite, candidate);
                    ImGui.PopID();
                }
                ImGui.EndCombo();
            }
            Theme.Tooltip(L.T("props.joinGroup.tip"));
            if (sprite.Group != null)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton(L.T("props.editGroup")))
                {
                    // To where the group is one thing, which is one step out of it.
                    var owner = sprite.Group;
                    app.OpenContainer(owner.Parent);
                    app.SelectGroup(owner);
                }
            }
        }
        OwnerCombo(sprite);

        changed |= LabeledPair(L.T("props.initialPosition"), ref sprite.X, ref sprite.Y, PositionSpec, L.T("props.initialPosition.tip"));

        if (ImGui.Checkbox(L.T("props.animation"), ref sprite.IsAnimation)) changed = true;
        if (sprite.IsAnimation)
        {
            double frames = sprite.FrameCount;
            if (LabeledNumber(L.T("props.frameCount"), ref frames, new NumberSpec(L.T("props.frameCount"), 0.1f, "%.0f", 1, 10000, true)))
            {
                sprite.FrameCount = (int)frames;
                changed = true;
            }
            changed |= LabeledNumber(L.T("props.frameDelay"), ref sprite.FrameDelay, new NumberSpec(L.T("props.frameDelay"), 1f, "%.1f ms", 1, 100000, Suffix: "ms"));
            int loopType = (int)sprite.LoopType;
            if (ImGui.Combo(L.T("props.loopType"), ref loopType, LoopTypeNames.Value, LoopTypeNames.Value.Length))
            {
                sprite.LoopType = (SbLoopType)loopType;
                changed = true;
            }
        }

        bool hidden=sprite.Hidden;
        if (ImGui.Checkbox(L.T("props.hidden"), ref hidden)) {sprite.Hidden=hidden;changed = true;}
        ImGui.SameLine();
        {
            bool locked = sprite.Locked;
            if (ImGui.Checkbox(L.T("props.locked"), ref locked))
            {
                sprite.Locked = locked;
                changed = true;
            }
        }
        ImGui.PopItemWidth();

        DrawCurrentState(sprite);

        ImGui.SeparatorText(L.T("props.commands"));
        changed |= DrawAddButtons(sprite, null);
        NextInRow(ImGui.GetStyle().ItemSpacing.X, SmallButtonWidth(L.T("props.sortByTime")));
        if (ImGui.SmallButton(L.T("props.sortByTime")))
        {
            var sorted = sprite.Nodes.OrderBy(NodeStart).ToList();
            sprite.Nodes.Clear();
            sprite.Nodes.AddRange(sorted);
            foreach (var node in sprite.Nodes)
                if (node is SbGroup g) g.Commands = g.Commands.OrderBy(c => c.Start).ToList();
            changed = true;
        }
        DegreesToggle();

        if (sprite.Nodes.Count == 0)
            Theme.TextDimmed(L.T("props.noCommands"));

        // The sprite's own properties first, then its loops and triggers in the order they are written in.
        if(sprite.Group!=null)
        {
            bool face=sprite.FaceCamera;
            if(ImGui.Checkbox(L.T("space.face"),ref face)) {sprite.FaceCamera=face; changed=true;}
            Theme.Tooltip(L.T("space.face.tip"));
            if(GroupSpace.UsesDepth(sprite.Group)) Theme.TextDimmed(L.T("space.depth",GroupSpace.Project(sprite,app.Time).Depth));
            changed |= DrawTracks(sprite.DepthCommands,0,true,null,null,true);
        }
        changed |= DrawTracks(sprite.Nodes, 0, true);

        SbNode? remove = null;
        for (int i = 0; i < sprite.Nodes.Count; i++)
        {
            ImGui.PushID(i);
            switch (sprite.Nodes[i])
            {
                case SbLoop loop:
                    if (DrawLoop(sprite, loop, ref changed)) remove = loop;
                    break;
                case SbTrigger trigger:
                    if (DrawTrigger(sprite, trigger, ref changed)) remove = trigger;
                    break;
            }
            ImGui.PopID();
        }

        if (remove != null)
        {
            sprite.Nodes.Remove(remove);
            app.SelectedKeys.Clear();
            changed = true;
        }
        if (changed) app.Touch(sprite);
    }

    /// <summary>What the times of a loop or trigger count from in the panel: the first pass of a loop, the simulated firing of a trigger at the playhead.</summary>
    private int GroupStart(SbGroup group) => group switch
    {
        SbLoop l => l.Start,
        SbTrigger t => app.BlockOffset(t),
        _ => 0,
    };

    private static int NodeStart(SbNode node) => node switch
    {
        SbCommand c => c.Start,
        SbLoop l => l.Start,
        SbTrigger t => t.Start,
        _ => 0,
    };

    private void DrawCurrentState(SbSprite sprite)
    {
        ImGui.SeparatorText(L.T("props.currentValues"));
        var timeline = sprite.Timeline;
        if (!timeline.HasCommands)
        {
            Theme.TextDimmed(timeline.HasTriggers ? L.T("props.onlyTriggers") : L.T("props.neverShown"));
            return;
        }

        var st = timeline.Evaluate(app.Time, sprite);
        bool active = timeline.IsActive(app.Time);
        ImGui.TextUnformatted(L.T("props.activeRange", Theme.FormatTime(timeline.StartTime), Theme.FormatTime(timeline.EndTime)));
        if (!active)
        {
            ImGui.SameLine();
            Theme.TextDimmed(L.T("props.notShownNow"));
        }
        double rotation = app.Settings.RotationInDegrees ? st.Rotation * 180 / Math.PI : st.Rotation;
        string flags = (st.FlipH ? "H " : "") + (st.FlipV ? "V " : "") + (st.Additive ? "A" : "");
        Theme.TextDimmed(
            L.T("props.state", st.X, st.Y, st.Scale, st.ScaleX, st.ScaleY, rotation, app.Settings.RotationInDegrees ? "°" : " rad", st.Opacity, st.R, st.G, st.B) +
            (flags.Length > 0 ? "   " + L.T("props.stateParams", flags) : ""));

        if (sprite.Group != null)
        {
            // The values above are the sprite's own; this is what is actually shown.
            var world = app.PreviewState(sprite, app.Time);
            double worldRotation = app.Settings.RotationInDegrees ? world.Rotation * 180 / Math.PI : world.Rotation;
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.GroupColor);
            Theme.TextWrapped(L.T("props.worldState", sprite.Group.Name, world.X, world.Y, world.Scale, worldRotation, app.Settings.RotationInDegrees ? "°" : " rad"));
            ImGui.PopStyleColor();
        }
    }

    private static float SmallButtonWidth(string label) => ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2;

    /// <summary>
    /// Goes on along the line for the next item of a row, or down to a new line when the item would not
    /// fit there: the panel is often narrower than a row of buttons.
    /// </summary>
    private static void NextInRow(float gap, float itemWidth)
    {
        ImGui.SameLine(0, gap);
        if (ImGui.GetContentRegionAvail().X < itemWidth) ImGui.NewLine();
    }

    /// <summary>The timeline uses the same buttons and editing actions as the properties panel.</summary>
    public void DrawCommandToolbar()
    {
        ImGui.PushID("timelineCommandAdd");
        if (app.CameraSelection is {} cameraGroup)
        {
            foreach(var kind in SbInfo.CameraKinds)
            {
                ImGui.SameLine();
                ImGui.BeginDisabled(!cameraGroup.CameraActive);
                if(ImGui.SmallButton(Theme.KindLabel(kind))) app.AddCameraKey(cameraGroup,kind);
                ImGui.EndDisabled();
            }
        }
        else if (app.SelectedGroup is { } group)
        {
            if (DrawGroupAddButtons(group)) app.TouchGroup(group);
        }
        else if (app.Primary is SbSprite sprite && app.SelectedGroups.Count == 0)
        {
            // The selected loop/trigger is the insertion target even when the properties panel's
            // display filter is off. All command kinds remain accessible from the timeline.
            var (block, _) = Picked(sprite, false);
            if (DrawAddButtons(sprite, block)) app.Touch(sprite);
        }
        else
        {
            ImGui.BeginDisabled();
            DrawAddButtons(null, null);
            ImGui.EndDisabled();
        }
        ImGui.PopID();
    }

    /// <summary>
    /// Row of buttons, one for each property: a keyframe at the playhead for the sprite or for one of its
    /// loops and triggers. P has no values to keep, so there it is a command that runs from the playhead on.
    /// </summary>
    /// <param name="only">The one kind to have a button for, while the panel shows that property alone.</param>
    private bool DrawAddButtons(SbSprite? sprite, SbGroup? group, CommandKind? only = null)
    {
        ImGui.BeginDisabled(sprite != null && app.IsProtectedBeatmapBackground(sprite));
        bool added = false;
        Theme.TextDimmed(L.T("props.addKey"));
        foreach (var kind in SbInfo.AllKinds.Concat(SbInfo.DepthKinds).Concat(SbInfo.CameraKinds))
        {
            if(SbInfo.CameraKinds.Contains(kind) || (SbInfo.DepthKinds.Contains(kind) && (sprite?.Group==null || group!=null))) continue;
            if (only != null && kind != only) continue;
            NextInRow(Theme.Px(4), SmallButtonWidth(kind.ToString()));
            if (UiScriptTargets.Item("properties.add." + kind, ImGui.SmallButton(kind.ToString())) && sprite != null)
            {
                var result = kind == CommandKind.P ? EditorApp.KeyResult.NotKeyframes : app.AddKey(sprite, group, kind);
                added |= result == EditorApp.KeyResult.NotKeyframes
                    ? AddCommand(sprite, group, kind)
                    : result == EditorApp.KeyResult.Done;
            }
            if (kind == CommandKind.P) Theme.Tooltip(Theme.KindLabel(kind) + "  " + L.T(group == null ? "props.fromNow" : "props.relativeToBlock"));
            else Theme.Tooltip(Theme.KindLabel(kind) + "\n" + L.T(group == null ? "props.addKey.tip" : "props.addKey.blockTip"));
        }
        if (group == null && only == null)
        {
            NextInRow(Theme.Px(10), SmallButtonWidth(L.T("props.addLoop")));
            if (ImGui.SmallButton(L.T("props.addLoop")) && sprite != null)
            {
                var loop = new SbLoop { Start = app.SnappedTime, Count = 4 };
                sprite.Nodes.Add(loop);
                app.SelectBlock(sprite, loop);
                added = true;
            }
            NextInRow(Theme.Px(4), SmallButtonWidth(L.T("props.addTrigger")));
            if (ImGui.SmallButton(L.T("props.addTrigger")) && sprite != null)
            {
                int start = app.SnappedTime;
                var trigger = new SbTrigger { Start = start, End = app.AfterBeats(start, 16) };
                sprite.Nodes.Add(trigger);
                app.SelectBlock(sprite, trigger);
                added = true;
            }
        }
        if (added) CommandAdded?.Invoke();
        ImGui.EndDisabled();
        return added;
    }

    /// <summary>
    /// Adds a command as such, running for two beats: a P command, or one more of a kind whose commands
    /// already run at the same time.
    /// </summary>
    private bool AddCommand(SbSprite sprite, SbGroup? group, CommandKind kind)
    {
        // Both ends land on snap ticks. Times inside a group are relative to its start, so the snapping is
        // done on the absolute time.
        int offset = group == null ? 0 : GroupStart(group);
        var nodes = group == null ? (IList)sprite.Nodes : group.Commands;
        char parameter = kind == CommandKind.P && app.ActiveTrack is { } track && track.Nodes == nodes ? track.Parameter : '\0';
        int start = parameter != '\0' ? Math.Max(group == null ? int.MinValue : 0, app.SnappedTime - app.BlockOffset(group))
            : group == null ? app.SnappedTime : group.Commands.Count == 0 ? 0 : group.CommandsEnd;
        int length = app.AfterBeats(offset + start, 2) - (offset + start);
        if (parameter != '\0' && group is SbLoop loop && start < loop.IterationLength) length = Math.Min(length, loop.IterationLength - start);
        var timeline = sprite.Timeline;

        var command = kind == CommandKind.P
            ? SbCommand.Parameter(start, start + length, parameter == '\0' ? 'A' : parameter)
            : new SbCommand(kind, 0, start, start + length, EditorApp.StateValues(kind, timeline.Evaluate(app.Time, sprite), timeline.HasCommands));

        if (app.RefusesCommand(group == null ? sprite.Nodes : group.Commands, command, offset)) return false;
        if (parameter != '\0')
        {
            var result = ParameterTrack.Add(nodes, parameter, start, start + length);
            app.SelectTrack(nodes, kind, parameter);
            app.SelectedCommands.Add(result.Command);
            return result.Added;
        }
        if (group != null) group.Commands.Add(command);
        else sprite.Nodes.Add(command);
        app.SelectTrack(group == null ? sprite.Nodes : group.Commands, kind, parameter);
        app.SelectedCommands.Add(command);
        return true;
    }

    // ---------------------------------------------------------------- keyframes

    /// <summary>
    /// The commands in a list, one property after the other. A property is shown as its keyframes: at this
    /// time it has this value, and it gets to the next keyframe in this way. Where that cannot be done the
    /// commands are shown as they are written: P commands have no value, and commands of one kind that run
    /// at the same time have no single value at a moment.
    /// </summary>
    /// <param name="offset">Start of the enclosing loop or trigger; 0 for a sprite or a group of sprites.</param>
    /// <param name="pickable">
    /// A property can be picked by its name: the panel then shows it alone, and the preview adjusts it where
    /// it can (not a colour or a parameter, and nothing of a trigger, which the preview does not play).
    /// </param>
    /// <param name="playheadOffset">
    /// What the playhead counts from when it is looked for among the keyframes: for a loop the start of the
    /// pass it is in. Left out, it is <paramref name="offset"/>.
    /// </param>
    /// <param name="only">The one kind to show; left out, all of them.</param>
    /// <param name="curves">
    /// A transition can be drawn by hand in place of an easing. That is so for a group of sprites alone:
    /// what a sprite has is written as osb, which knows the easings and nothing else.
    /// </param>
    private bool DrawTracks(IList nodes, int offset, bool pickable, int? playheadOffset = null, CommandKind? only = null, bool curves = false)
    {
        bool changed = false;
        foreach (var kind in SbInfo.AllKinds.Concat(SbInfo.DepthKinds).Concat(SbInfo.CameraKinds))
        {
            if (only != null && kind != only) continue;
            var commands = KeyTrack.Commands(nodes, kind);
            if (only == CommandKind.P && app.ActiveTrack is { Parameter: not '\0' } lane && lane.Nodes == nodes)
                commands = ParameterTrack.Commands(nodes, lane.Parameter);
            if (commands.Count == 0) continue;
            var keys = kind == CommandKind.P ? null : KeyTrack.Read(commands);

            ImGui.PushID((int)kind);
            Vector2 pos = ImGui.GetCursorScreenPos();
            ImGui.GetWindowDrawList().AddRectFilled(pos + new Vector2(0, 1), pos + new Vector2(Theme.Px(3), ImGui.GetFontSize() + 1), Theme.KindColor(kind));
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.Px(8));
            if (pickable)
            {
                // Picked, it is what the panel shows alone and what dragging in the preview adjusts; picked
                // again, it is let go of.
                bool active = app.IsActiveTrack(nodes, kind);
                if (ImGui.Selectable(Theme.KindLabel(kind) + "##track", active))
                {
                    if (active) app.ActiveTrack = null;
                    else app.SelectTrack(nodes, kind);
                }
                Theme.Tooltip(kind == CommandKind.P ? L.T("timeline.track.pickTip") : L.T("timeline.track.tip"));
            }
            else
            {
                ImGui.TextUnformatted(Theme.KindLabel(kind));
            }
            if (keys == null && kind != CommandKind.P)
            {
                ImGui.SameLine();
                Theme.TextDimmed(L.T("props.track.raw"));
                Theme.Tooltip(L.T("props.track.raw.tip"));
            }
            if (CommandClash.Overlaps(nodes, kind).Count > 0)
            {
                // It runs together with a kind it may not run with: the timeline shows where, in red.
                ImGui.SameLine();
                ImGui.TextColored(ImGui.ColorConvertU32ToFloat4(Theme.TextError), L.T("props.track.clash", string.Join(" / ", CommandClash.Rivals(kind))));
                Theme.Tooltip(L.T("props.track.clash.tip"));
            }

            if (keys != null)
            {
                changed |= DrawKeys(nodes, kind, keys, offset, playheadOffset ?? offset, curves);
            }
            else
            {
                SbCommand? remove = null;
                for (int i = 0; i < commands.Count; i++)
                {
                    ImGui.PushID(i);
                    if (DrawCommand(commands[i], ref changed, offset, curves)) remove = commands[i];
                    ImGui.PopID();
                }
                if (remove != null)
                {
                    nodes.Remove(remove);
                    app.SelectedCommands.Remove(remove);
                    changed = true;
                }
            }
            ImGui.PopID();
        }
        return changed;
    }

    /// <summary>The keyframes of one property: two lines each, the time and the way on to the next one above the value.</summary>
    private bool DrawKeys(IList nodes, CommandKind kind, List<Keyframe> keys, int offset, int playheadOffset, bool curves = false)
    {
        var style = ImGui.GetStyle();
        float width = ImGui.GetContentRegionAvail().X;
        float frame = ImGui.GetFrameHeight();
        float spacing = style.ItemSpacing.X;
        float markWidth = Theme.Px(22);
        float rest = MathF.Max(120, width - markWidth - frame - spacing * 3);
        // The time gets the room its caption needs before the easing gets more than it needs.
        string timeCaption = L.T("value.time"), easingCaption = L.T("props.caption.easing");
        float timeWidth = MathF.Round(MathF.Max(rest * 0.4f, MathF.Min(TimeFieldWidth(timeCaption), rest - Theme.Px(90)))), easeWidth = rest - timeWidth;
        float blockHeight = frame * 2 + style.ItemSpacing.Y;
        var dl = ImGui.GetWindowDrawList();
        int playhead = (int)Math.Round(app.Time) - playheadOffset;
        bool changed = false;

        for (int i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            var reference = new KeyRef(nodes, kind, i);
            ImGui.PushID(i);
            Vector2 pos = ImGui.GetCursorScreenPos();
            if (app.SelectedKeys.Contains(reference))
                dl.AddRectFilled(pos - new Vector2(3, 2), pos + new Vector2(width + 3, blockHeight + 2), Theme.AccentDim, 3);
            // One that was just selected, in the timeline say, is brought into view where it is out of it.
            if (app.SelectedKeys.Count > 0 && app.SelectedKeys[^1] == reference && shownKey != reference)
            {
                shownKey = reference;
                if (!ImGui.IsRectVisible(new Vector2(width, blockHeight))) ImGui.SetScrollHereY(0.3f);
            }

            // Line 1: the keyframe, its time, the way on to the next keyframe, delete.
            if (ImGui.Selectable("##key", false, ImGuiSelectableFlags.AllowDoubleClick, new Vector2(markWidth, frame)))
            {
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) app.Seek(offset + key.Time);
                else if (!ImGui.GetIO().KeyCtrl) app.SelectKey(reference);
                else if (!app.SelectedKeys.Remove(reference)) app.SelectedKeys.Add(reference);
            }
            Theme.Tooltip(L.T("props.key.tip"));
            // The colour of its kind, or its own for a colour; outlined in white while the playhead stands on it.
            uint fill = kind == CommandKind.C && key.Value.Length == 3 ? Theme.Rgba((int)key.Value[0], (int)key.Value[1], (int)key.Value[2]) : Theme.KindColor(kind);
            Theme.Diamond(dl, pos + new Vector2(markWidth / 2, frame / 2), Theme.Px(5), fill, key.Time == playhead ? Theme.Rgba(255, 255, 255) : Theme.Rgba(0, 0, 0, 170));

            ImGui.SameLine();
            int time = key.Time;
            var (min, max) = app.KeyTimeRange(keys, i, offset);
            if (TimeField("##time", ref time, timeWidth, offset, min, max, L.T("props.key.time"), timeCaption, keyTimeFormat))
            {
                KeyTrack.SetTime(key, Math.Clamp(time, min, max));
                changed = true;
            }

            bool rewritten = false;
            ImGui.SameLine();
            ImGui.SetNextItemWidth(easeWidth);
            if (i == keys.Count - 1)
            {
                // Nothing comes after the last keyframe, so there is no way of getting there to choose.
                int only = 0;
                ImGui.BeginDisabled();
                ImGui.Combo("##easing", ref only, LastKeyLabel.Value, 1);
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled | ImGuiHoveredFlags.DelayShort)) Theme.SetTooltip(L.T("props.key.last.tip"));
            }
            else
            {
                // The first entry is no transition at all, which the keyframe keeps as -1. For a group of
                // sprites the last one is a curve drawn by hand.
                string[] labels = curves ? CurveTransitionLabels.Value : TransitionLabels.Value;
                int custom = curves ? labels.Length - 1 : -1;
                int choice = curves && key.Curve != null ? custom : key.Easing + 1;
                string tip = L.T("props.key.easing", labels[Math.Clamp(choice, 0, labels.Length - 1)]);
                // What the curve is, or what it would start out as: the easing it takes the place of.
                Func<EasingCurve>? drawn = curves ? () => key.Curve ?? StartCurve(key.Easing) : null;
                if (CaptionCombo("##easing", easingCaption, ref choice, labels, 14, tip, Keyframe.Hold, drawn))
                {
                    if (choice != custom)
                    {
                        if (!app.RefusesEasing(nodes, kind, i, choice - 1, offset)) rewritten = KeyTrack.SetEasing(nodes, kind, i, choice - 1);
                    }
                    else if (key.Curve == null && !app.RefusesEasing(nodes, kind, i, 0, offset))
                    {
                        rewritten = KeyTrack.SetEasing(nodes, kind, i, 0, EasingCurve.From(key.Easing));
                    }
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("×", new Vector2(frame, frame)))
            {
                rewritten = KeyTrack.Remove(nodes, kind, i);
                app.SelectedKeys.Clear();
            }
            Theme.Tooltip(L.T("props.key.delete"));

            // Line 2: the value. Not for a track that has just been written anew: its keyframes are others now.
            if (!rewritten)
            {
                ImGui.Dummy(new Vector2(markWidth, frame));
                ImGui.SameLine();
                var value = (double[])key.Value.Clone();
                if (ValueField("##value", kind, value, MathF.Max(40, width - markWidth - spacing), ColourSetter(v => KeyTrack.SetValue(key, v))))
                {
                    KeyTrack.SetValue(key, value);
                    changed = true;
                }
                // Line 3, for a transition drawn by hand: the curve, with its points to take hold of.
                if (key.Curve != null && i < keys.Count - 1) changed |= DrawCurve(key.Curve, markWidth, width);
            }
            ImGui.PopID();
            if (rewritten) return true;
        }
        return changed;
    }

    // ---------------------------------------------------------------- commands

    /// <param name="offset">Start of the enclosing loop or trigger; 0 for top-level commands.</param>
    /// <param name="curves">The command of a group of sprites, which can have a transition drawn by hand.</param>
    /// <returns>True when the command should be deleted.</returns>
    private bool DrawCommand(SbCommand c, ref bool changed, int offset = 0, bool curves = false)
    {
        var style = ImGui.GetStyle();
        float width = ImGui.GetContentRegionAvail().X;
        float frame = ImGui.GetFrameHeight();
        float spacing = style.ItemSpacing.X;
        float kindWidth = Theme.Px(30);
        float arrowWidth = ImGui.CalcTextSize("→").X;
        float rest = MathF.Max(120, width - kindWidth - frame - arrowWidth - spacing * 5);
        // The two times get the room their captions need. Where one line does not have it, as in a
        // narrow panel, they go on a line of their own under the easing.
        string startCaption = L.T("props.caption.start"), endCaption = L.T("props.caption.end");
        float timeNeed = MathF.Max(TimeFieldWidth(startCaption), TimeFieldWidth(endCaption));
        bool stacked = timeNeed * 2 + Theme.Px(100) > rest;
        float timeWidth = MathF.Floor(MathF.Max(rest * 0.30f, timeNeed)), easeWidth = rest - timeWidth * 2;
        if (stacked)
        {
            easeWidth = MathF.Max(40, width - kindWidth - frame - spacing * 2);
            timeWidth = MathF.Max(40, MathF.Floor((width - kindWidth - arrowWidth - spacing * 3) / 2));
        }

        bool selected = app.SelectedCommands.Contains(c);
        Vector2 pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        // A parameter has no values, and so no line for them.
        int lines = (c.Kind == CommandKind.P ? 1 : 2) + (stacked ? 1 : 0);
        float blockHeight = frame * lines + style.ItemSpacing.Y * (lines - 1);
        if (selected)
            dl.AddRectFilled(pos - new Vector2(3, 2), pos + new Vector2(width + 3, blockHeight + 2), Theme.AccentDim, 3);
        dl.AddRectFilled(pos - new Vector2(4, 0), pos + new Vector2(-1, blockHeight), Theme.KindColor(c.Kind));
        // The first of the selected ones is brought into view when it is another than before and out of sight:
        // selected in the timeline, or gone to from the list of problems.
        if (selected && firstSelected == null)
        {
            firstSelected = c;
            if (shownCommand != c && !ImGui.IsRectVisible(new Vector2(width, blockHeight))) ImGui.SetScrollHereY(0.3f);
            shownCommand = c;
        }

        // Line 1: kind, easing, start -> end, delete. Stacked, the times are a second line.
        ImGui.AlignTextToFramePadding();
        if (ImGui.Selectable(c.Kind.ToString(), false, ImGuiSelectableFlags.None, new Vector2(kindWidth, frame)))
        {
            if (!ImGui.GetIO().KeyCtrl)
            {
                app.SelectedCommands.Clear();
                app.SelectedKeys.Clear();
            }
            if (!app.SelectedCommands.Remove(c)) app.SelectedCommands.Add(c);
        }
        Theme.Tooltip(Theme.KindLabel(c.Kind) + "\n" + L.T("props.command.tip"));

        ImGui.SameLine();
        ImGui.SetNextItemWidth(easeWidth);
        if (c.Kind == CommandKind.P)
        {
            // A parameter is on or off; which one it is stands where the others have their easing.
            int index = Math.Max(0, "HVA".IndexOf(char.ToUpperInvariant(c.Param)));
            if (ImGui.Combo("##param", ref index, ParamNames.Value, ParamNames.Value.Length))
            {
                c.Param = "HVA"[index];
                changed = true;
            }
        }
        else if (curves || c.Curve != null)
        {
            // The command of a group of sprites: the last entry is a transition drawn by hand, which
            // starts out as the easing it takes the place of. An easing chosen in its place takes it away.
            string[] labels = CurveEasingLabels.Value;
            int custom = labels.Length - 1, index = c.Curve != null ? custom : Math.Clamp(c.Easing, 0, custom - 1);
            if (CaptionCombo("##easing", L.T("props.caption.easing"), ref index, labels, 14, L.T("props.easing", labels[index]), 0,
                    () => c.Curve ?? StartCurve(c.Easing)))
            {
                if (index != custom)
                {
                    c.Easing = index;
                    c.Curve = null;
                    changed = true;
                }
                else if (c.Curve == null)
                {
                    c.Curve = EasingCurve.From(c.Easing);
                    c.Easing = 0;
                    changed = true;
                }
            }
        }
        else
        {
            if (CaptionCombo("##easing", L.T("props.caption.easing"), ref c.Easing, EasingLabels, 14,
                    L.T("props.easing", EasingLabels[Math.Clamp(c.Easing, 0, EasingLabels.Length - 1)]), 0)) changed = true;
        }

        bool remove = false;
        if (stacked)
        {
            RemoveButton();
            ImGui.Dummy(new Vector2(kindWidth, frame));
        }
        ImGui.SameLine();
        // A command cannot end before it starts: dragging stops there, typing such a time is refused.
        changed |= TimeField("##start", ref c.Start, timeWidth, offset, int.MinValue, c.End, L.T("props.startTime"), startCaption);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("→");
        ImGui.SameLine();
        changed |= TimeField("##end", ref c.End, timeWidth, offset, c.Start, int.MaxValue, L.T("props.endTime"), endCaption);
        if (!stacked) RemoveButton();

        void RemoveButton()
        {
            ImGui.SameLine();
            remove = ImGui.Button("×", new Vector2(frame, frame));
            Theme.Tooltip(L.T("props.deleteCommand"));
        }

        if (c.Kind == CommandKind.P)
        {
            if (c.End == c.Start)
            {
                ImGui.Dummy(new Vector2(kindWidth, 0));
                ImGui.SameLine();
                Theme.TextDimmed(L.T("props.paramWhole"));
            }
            return remove;
        }

        // Line 2: values.
        ImGui.Dummy(new Vector2(kindWidth, frame));
        ImGui.SameLine();
        float eqWidth = frame;
        float valueWidth = MathF.Max(40, (width - kindWidth - eqWidth - spacing * 3) / 2);

        changed |= ValueField("##from", c.Kind, c.From, valueWidth, ColourSetter(v => c.From = (double[])v.Clone()));
        ImGui.SameLine();
        if (ImGui.Button("=", new Vector2(eqWidth, frame)))
        {
            Array.Copy(c.From, c.To, Math.Min(c.From.Length, c.To.Length));
            changed = true;
        }
        Theme.Tooltip(L.T("props.copyValue"));
        ImGui.SameLine();
        changed |= ValueField("##to", c.Kind, c.To, valueWidth, ColourSetter(v => c.To = (double[])v.Clone()));
        if (c.Curve != null) changed |= DrawCurve(c.Curve, kindWidth, width);
        return remove;
    }

    /// <summary>
    /// What a curve drawn by hand starts out as in place of each easing, for the picture the list shows of
    /// it before it is chosen. Worked out once; a command gets a curve of its own when it is chosen.
    /// </summary>
    private static readonly Dictionary<int, EasingCurve> StartCurves = new();

    private static EasingCurve StartCurve(int easing)
    {
        if (!StartCurves.TryGetValue(easing, out var curve)) StartCurves[easing] = curve = EasingCurve.From(easing);
        return curve;
    }

    /// <summary>
    /// A transition drawn by hand under the keyframe or command it belongs to: the curve with its points
    /// (<see cref="CurveEditor"/>), and under it the way back to a straight line and, in a few words, how
    /// to go about it; the rest of that is said when the mouse rests on the words.
    /// </summary>
    /// <param name="indent">How far in the fields above it begin.</param>
    /// <param name="width">The width of the keyframe or command as a whole.</param>
    private static bool DrawCurve(EasingCurve curve, float indent, float width)
    {
        bool changed = false;
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        ImGui.Dummy(new Vector2(indent, 0));
        ImGui.SameLine();
        ImGui.BeginGroup();
        float room = MathF.Max(40, width - indent - spacing);
        changed |= CurveEditor.Draw("##curve", curve, room);
        ImGui.BeginDisabled(curve.Points.Count == 0);
        if (ImGui.SmallButton(L.T("curve.reset")))
        {
            curve.Points.Clear();
            changed = true;
        }
        ImGui.EndDisabled();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + room);
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
        ImGui.TextWrapped(Theme.EscapeFormat(L.T("curve.hint")));
        ImGui.PopStyleColor();
        ImGui.PopTextWrapPos();
        Theme.Tooltip(L.T("curve.hint.more"));
        ImGui.EndGroup();
        return changed;
    }

    /// <param name="offset">Absolute time of value 0: the start of the loop or trigger the command belongs to.</param>
    /// <param name="min">Limits in the field's own terms; typed times outside them are refused.</param>
    /// <param name="caption">What the field says it is inside its own box, ahead of the time.</param>
    private bool TimeField(string id, ref int value, float width, int offset = 0, int min = int.MinValue, int max = int.MaxValue, string? name = null, string caption = "", string format = "%d")
    {
        bool changed = TimeInput.Field(app, id, ref value, width, offset, format, min, max, name, caption);
        bool hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal);
        var rectMin = ImGui.GetItemRectMin();
        var rectMax = ImGui.GetItemRectMax();
        bool offGrid = app.HasTiming && app.GridMs(offset + value) != offset + value;
        if (ImGui.BeginPopupContextItem(id + "ctx"))
        {
            if (ImGui.MenuItem(L.T("time.setCurrent")))
            {
                value = app.SnappedTime - offset;
                changed = true;
            }
            if (ImGui.MenuItem(L.T("time.snap"), null, false, offGrid))
            {
                value = app.GridMs(offset + value) - offset;
                changed = true;
            }
            if (ImGui.MenuItem(L.T("time.goto"))) app.Seek(offset + value);
            ImGui.EndPopup();
        }
        if (offGrid)
        {
            // Typed values may sit between ticks; mark them so they are easy to spot.
            ImGui.GetWindowDrawList().AddLine(new Vector2(rectMin.X + 2, rectMax.Y - 1), new Vector2(rectMax.X - 2, rectMax.Y - 1), Theme.Rgba(235, 200, 90));
        }
        if (hovered)
            Theme.SetTooltip(Theme.FormatTime(offset + value) + (offGrid ? "\n" + L.T("time.offGrid") : "") +
                             "\n" + L.T("time.tip"));
        return changed;
    }

    /// <summary>How wide a time field has to be for its caption to stand in it next to a time of six digits.</summary>
    private static float TimeFieldWidth(string caption) =>
        ImGui.CalcTextSize(caption).X + ImGui.CalcTextSize("888888").X + Theme.Px(6) + ImGui.GetStyle().FramePadding.X * 2 + 2;

    /// <summary>
    /// A dropdown that says what it chooses inside its own box, ahead of the choice, the way the number
    /// fields do: a row of boxes then reads "Time 1200  Easing 0 Linear" and not just "1200  0 Linear".
    /// </summary>
    /// <param name="visibleItems">How many entries the open list shows before it scrolls.</param>
    /// <param name="firstEasing">
    /// For a list of easings, the easing its first entry stands for (the others follow in order): with the
    /// mouse on an entry, or on the closed box, a picture of that easing is shown, see <see cref="EasingPreview"/>.
    /// </param>
    /// <param name="drawn">
    /// For a list whose last entry is a transition drawn by hand: the curve to show for that entry. It is
    /// only asked for while the mouse is on the entry.
    /// </param>
    private static bool CaptionCombo(string id, string caption, ref int index, string[] items, int visibleItems, string? tooltip = null, int? firstEasing = null,
        Func<EasingCurve>? drawn = null)
    {
        var style = ImGui.GetStyle();
        Vector2 min = ImGui.GetCursorScreenPos();
        float width = ImGui.CalcItemWidth(), frame = ImGui.GetFrameHeight();
        // The list opens as a window of its own; what is written into the box goes to this one.
        var dl = ImGui.GetWindowDrawList();
        string shown = items[Math.Clamp(index, 0, items.Length - 1)];

        // As tall as ImGui.Combo makes a list of that many entries.
        ImGui.SetNextWindowSizeConstraints(Vector2.Zero,
            new Vector2(float.MaxValue, (ImGui.GetFontSize() + style.ItemSpacing.Y) * visibleItems - style.ItemSpacing.Y + style.WindowPadding.Y * 2));
        // The box is drawn without a text of its own; what it says is written below, caption first.
        bool open = ImGui.BeginCombo(id, "");
        if (!open && firstEasing is { } chosen)
        {
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            {
                int at = Math.Clamp(index, 0, items.Length - 1);
                EasingPreview.Tooltip(chosen + at, shown, tooltip, null, drawn != null && at == items.Length - 1 ? drawn() : null);
            }
        }
        else if (!open && tooltip != null) Theme.Tooltip(tooltip);

        float left = min.X + style.FramePadding.X, right = min.X + width - frame - style.FramePadding.X;
        float captionWidth = ImGui.CalcTextSize(caption).X, gap = Theme.Px(6);
        // A long name may be cut off at the arrow, as it would be without a caption; a box with no room
        // even for the start of the name goes without the caption instead.
        bool fits = captionWidth + gap + MathF.Min(ImGui.CalcTextSize(shown).X, Theme.Px(56)) <= right - left;
        dl.PushClipRect(min, new Vector2(right, min.Y + frame), true);
        float y = min.Y + style.FramePadding.Y;
        if (fits) dl.AddText(new Vector2(left, y), ImGui.GetColorU32(Theme.TextDim), caption);
        dl.AddText(new Vector2(fits ? left + captionWidth + gap : left, y), ImGui.GetColorU32(ImGuiCol.Text), shown);
        dl.PopClipRect();

        bool changed = false;
        if (!open) return false;
        for (int i = 0; i < items.Length; i++)
        {
            bool selected = i == index;
            if (ImGui.Selectable(items[i], selected) && !selected)
            {
                index = i;
                changed = true;
            }
            if (selected) ImGui.SetItemDefaultFocus();
            // What the entry under the mouse looks like, next to the list.
            if (firstEasing is { } first && ImGui.IsItemHovered())
                EasingPreview.Tooltip(first + i, items[i], null, (ImGui.GetWindowPos(), ImGui.GetWindowPos() + ImGui.GetWindowSize()),
                    drawn != null && i == items.Length - 1 ? drawn() : null);
        }
        ImGui.EndCombo();
        return changed;
    }

    private Action<double[]> ColourSetter(Action<double[]> set)
    {
        var owner = app.Primary;
        return values => { set(values); app.Touch(owner); };
    }

    private bool ValueField(string id, CommandKind kind, double[] values, float width, Action<double[]> setColour)
    {
        switch (kind)
        {
            case CommandKind.F:
                return NumberInput.Field(app, id, ref values[0], width, OpacitySpec with { Caption = L.T("value.opacity") });

            case CommandKind.M:
                return NumberInput.Pair(app, id, ref values[0], ref values[1], width, PositionSpec);

            case CommandKind.MX:
                return NumberInput.Field(app, id, ref values[0], width, PositionSpec with { Name = L.T("value.positionX"), Caption = "X" });

            case CommandKind.MY:
                return NumberInput.Field(app, id, ref values[0], width, PositionSpec with { Name = L.T("value.positionY"), Caption = "Y" });

            case CommandKind.S:
            {
                string fitLabel = L.T("props.scaleAutoFit");
                float buttonWidth = SmallButtonWidth(fitLabel);
                float spacing = ImGui.GetStyle().ItemInnerSpacing.X;
                bool changed = NumberInput.Field(app, id, ref values[0], MathF.Max(20, width - buttonWidth - spacing),
                    ScaleSpec with { Caption = L.T("value.scale") });
                ImGui.SameLine(0, spacing);
                var sprite = app.Primary as SbSprite;
                double? fittedScale = sprite == null ? null : app.ImageFitScale(sprite);
                ImGui.BeginDisabled(fittedScale == null);
                if (ImGui.Button(fitLabel + id + "fit", new Vector2(buttonWidth, 0)) && fittedScale is { } scale)
                {
                    // Match osu!'s background fill: preserve aspect ratio and cover the game frame.
                    if (values[0] != scale)
                    {
                        values[0] = scale;
                        changed = true;
                    }
                }
                ImGui.EndDisabled();
                Theme.Tooltip(L.T("props.scaleAutoFit.tip"));
                return changed;
            }

            case CommandKind.V:
                // The two factors of a vector scale: how wide and how high.
                return NumberInput.Pair(app, id, ref values[0], ref values[1], width, ScaleSpec, L.T("props.caption.width"), L.T("props.caption.height"));

            case CommandKind.RX: case CommandKind.RY: case CommandKind.CO: case CommandKind.CI: case CommandKind.CL:
            case CommandKind.R:
            {
                string caption = L.T("value.rotation");
                if (!app.Settings.RotationInDegrees)
                    return NumberInput.Field(app, id, ref values[0], width, RadiansSpec with { Caption = caption });
                // Shown and typed in degrees, stored in radians.
                double degrees = Math.Round(values[0] * 180 / Math.PI, 3);
                if (!NumberInput.Field(app, id, ref degrees, width, DegreesSpec with { Caption = caption })) return false;
                values[0] = Math.Round(degrees * Math.PI / 180, 5);
                return true;
            }

            case CommandKind.Z: case CommandKind.CD: case CommandKind.CF:
                return NumberInput.Field(app,id,ref values[0],width,KeyValueSpec(kind,app.Settings.RotationInDegrees));
            case CommandKind.CT:
            {
                bool changed=false;
                for(int i=0;i<3;i++) { if(i>0) ImGui.SameLine(); changed |= NumberInput.Field(app,id+i,ref values[i],Math.Max(20,width/3-4),PositionSpec); }
                return changed;
            }
            case CommandKind.C:
            {
                // A swatch that opens the colour picker, then the three components as numbers.
                var style = ImGui.GetStyle();
                float swatch = ImGui.GetFrameHeight();
                float each = MathF.Max(20, (width - swatch - style.ItemInnerSpacing.X * 3) / 3);
                bool changed = false;

                var colour = new Vector3((float)values[0] / 255, (float)values[1] / 255, (float)values[2] / 255);
                if (ImGui.ColorButton(id + "swatch", new Vector4(colour, 1), ImGuiColorEditFlags.NoAlpha, new Vector2(swatch)))
                    colourPicker.Open(id + "picker", values, setColour, this);
                colourPicker.Draw(id + "picker");
                string[] names = { L.T("value.red"), L.T("value.green"), L.T("value.blue") };
                for (int i = 0; i < 3; i++)
                {
                    ImGui.SameLine(0, style.ItemInnerSpacing.X);
                    changed |= NumberInput.Field(app, id + i, ref values[i], each, ColourSpec with { Name = names[i], Caption = "RGB".Substring(i, 1) });
                }
                return changed;
            }
        }
        return false;
    }

    private bool DrawLoop(SbSprite sprite, SbLoop loop, ref bool changed)
    {
        bool open = GroupHeader(loop, L.T("props.loopHeader", loop.Iterations, Theme.FormatTime(loop.AbsoluteStart), Theme.FormatTime(loop.AbsoluteEnd)) + "###loop", out bool remove);
        if (!open) return remove;

        string startLabel = L.T("props.loopStart"), countLabel = L.T("props.loopCount");
        float half = (ImGui.GetContentRegionAvail().X - Theme.Px(52) - ImGui.CalcTextSize(startLabel).X - ImGui.CalcTextSize(countLabel).X) / 2;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(startLabel);
        ImGui.SameLine();
        changed |= TimeField("##loopstart", ref loop.Start, MathF.Max(50, half), 0, int.MinValue, int.MaxValue, L.T("props.loopStartTime"));
        ImGui.SameLine();
        ImGui.TextUnformatted(countLabel);
        ImGui.SameLine();
        changed |= NumberInput.Field(app, "##count", ref loop.Count, MathF.Max(50, half), new NumberSpec(L.T("props.loopCountName"), 0.1f, "%.0f", 1, 100000));
        Theme.TextDimmed(L.T("props.loopNote", loop.IterationLength));

        DrawGroupBody(sprite, loop, ref changed);
        ImGui.TreePop();
        return remove;
    }

    /// <summary>The loop or trigger that was selected as a whole when the panel was last drawn.</summary>
    private SbGroup? shownBlock;

    private bool GroupHeader(SbGroup block, string label, out bool remove)
    {
        float frame = ImGui.GetFrameHeight();
        // Selected in the timeline, it is marked here and brought into view, unfolded.
        if (app.SelectedBlock == null) shownBlock = null;
        bool selected = app.SelectedBlock == block;
        if (selected && shownBlock != block)
        {
            ImGui.SetNextItemOpen(true, ImGuiCond.Always);
            // Shown alone it is at the top of the panel already.
            if (shownPart.Block != block) ImGui.SetScrollHereY(0.15f);
        }
        else ImGui.SetNextItemOpen(true, ImGuiCond.Once);
        if (selected) shownBlock = block;
        if (selected) ImGui.PushStyleColor(ImGuiCol.Header, Theme.AccentDim);
        bool open = ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.Framed | ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.AllowOverlap);
        if (selected) ImGui.PopStyleColor();
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - frame);
        remove = ImGui.SmallButton("×");
        Theme.Tooltip(L.T("props.deleteBlock"));
        return open;
    }

    private void DrawGroupBody(SbSprite sprite, SbGroup group, ref bool changed)
    {
        changed |= DrawAddButtons(sprite, group);
        // A trigger's times count from the moment it fires: they are shown as that much later.
        keyTimeFormat = group is SbTrigger ? "+%d ms" : "%d";
        changed |= DrawTracks(group.Commands, GroupStart(group), true, app.BlockOffset(group));
        keyTimeFormat = "%d";
    }

    /// <summary>How the times of keyframes read in the list being drawn.</summary>
    private string keyTimeFormat = "%d";

    // ---------------------------------------------------------------- sample

    private void DrawSample(SbSample sample)
    {
        bool changed = false;
        ImGui.PushItemWidth(-Theme.LabelWidth(78, L.T("props.name"), L.T("props.file"), L.T("value.time"), L.T("props.layer"), L.T("props.volume"), L.T("props.owner")));
        ImGui.SeparatorText(L.T("props.sample"));

        string name = sample.Name ?? "";
        if (ImGui.InputTextWithHint(L.T("props.name"), sample.DisplayName, ref name, 128))
        {
            sample.Name = name.Length == 0 ? null : name;
            changed = true;
        }
        ImGui.PushItemWidth(-1);
        changed |= AssetCombo("##samplePath", ref sample.Path, AssetKind.Audio, dropdownStyle: true);
        Theme.Tooltip(L.T("props.file") + ": " + sample.Path);
        ImGui.PopItemWidth();
        if (!app.Project!.AssetExists(sample.Path))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError);
            Theme.TextWrapped(L.T("props.fileMissing"));
            ImGui.PopStyleColor();
        }

        changed |= TimeField("##sampletime", ref sample.Time, ImGui.CalcItemWidth(), 0, int.MinValue, int.MaxValue, L.T("props.sampleTime"));
        ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.TextUnformatted(L.T("value.time"));

        int layer = (int)sample.Layer;
        if (ImGui.Combo(L.T("props.layer"), ref layer, Theme.LayerNames, 4))
        {
            sample.Layer = (SbLayer)layer;
            changed = true;
        }
        Theme.Tooltip(L.T("props.sampleLayer.tip"));
        OwnerCombo(sample);

        changed |= DrawSampleVolume(sample);
        bool hidden=sample.Hidden;
        if (ImGui.Checkbox(L.T("props.mute"), ref hidden)) {sample.Hidden=hidden;changed = true;}
        ImGui.PopItemWidth();

        if (changed) app.Touch(sample);
    }

    private bool DrawSampleVolume(SbSample sample)
    {
        float width = ImGui.CalcItemWidth(), gap = ImGui.GetStyle().ItemInnerSpacing.X;
        float inputWidth = MathF.Min(Theme.Px(70), width / 3);
        float volume = (float)sample.Volume;
        bool changed = ProgressSlider.Range("##sampleVolumeSlider", ref volume, 1, 100, MathF.Max(1, width - inputWidth - gap));
        if (changed) sample.Volume = volume;
        Theme.Tooltip(L.T("props.volume.tip"));
        ImGui.SameLine(0, gap);
        changed |= NumberInput.Field(app, "##sampleVolumeValue", ref sample.Volume, inputWidth,
            new NumberSpec(L.T("props.volume"), 0.5f, "%.0f", 1, 100, true));
        Theme.Tooltip(L.T("props.volume.tip"));
        ImGui.SameLine(0, gap);
        ImGui.TextUnformatted(L.T("props.volume"));
        return changed;
    }

    // ---------------------------------------------------------------- video

    private void DrawVideo(SbVideo video)
    {
        bool changed = false;
        ImGui.PushItemWidth(-Theme.LabelWidth(78, L.T("props.name"), L.T("props.file"), L.T("value.time"), L.T("props.owner")));
        ImGui.SeparatorText(L.T("props.video"));

        string name = video.Name ?? "";
        if (ImGui.InputTextWithHint(L.T("props.name"), video.DisplayName, ref name, 128))
        {
            video.Name = name.Length == 0 ? null : name;
            changed = true;
        }
        ImGui.PushItemWidth(-1);
        changed |= AssetCombo("##videoPath", ref video.Path, AssetKind.Video, dropdownStyle: true);
        Theme.Tooltip(L.T("props.file") + ": " + video.Path);
        ImGui.PopItemWidth();

        changed |= TimeField("##videotime", ref video.Time, ImGui.CalcItemWidth(), 0, int.MinValue, int.MaxValue, L.T("props.videoTime"));
        ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.TextUnformatted(L.T("value.time"));
        Theme.Tooltip(L.T("props.videoTime.tip"));
        OwnerCombo(video);
        bool hidden=video.Hidden;
        if (ImGui.Checkbox(L.T("props.videoHidden"), ref hidden)) {video.Hidden=hidden;changed = true;}
        ImGui.PopItemWidth();

        // What the file is like, or why it does not show.
        if (video.Path.Length > 0)
        {
            string file = app.Project!.AssetFullPath(video.Path);
            if (app.Videos.Info(file) is { } info)
            {
                string length = double.IsInfinity(info.DurationMs) ? L.T("props.videoLengthUnknown") : Theme.FormatTime((int)Math.Round(info.DurationMs));
                Theme.TextDimmed(L.T("props.videoInfo", info.Width, info.Height, length));
            }
            else if (app.Videos.Problem(file) is { } problem)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError);
                Theme.TextWrapped(problem);
                ImGui.PopStyleColor();
            }
        }
        Theme.TextDimmed(L.T("props.videoNote"));

        if (changed) app.Touch(video);
    }

    /// <summary>
    /// Which file the object's script belongs in: the .osb all difficulties share, or the .osu of the
    /// difficulty that is shown. Only there in a project that has difficulties.
    /// </summary>
    private void OwnerCombo(SbObject obj)
    {
        if (app.Project!.CurrentDifficulty is not { } current) return;
        int owner = obj.Difficulty == null ? 0 : 1;
        string items = L.T("props.ownerShared") + "\0" + L.T("props.ownerDifficulty", current.DisplayName) + "\0";
        if (ImGui.Combo(L.T("props.owner"), ref owner, items)) app.SetOwnDifficulty(obj, owner == 1);
        Theme.Tooltip(L.T("props.owner.tip"));
    }

    /// <summary>Editable path or searchable dropdown listing the project's assets of one kind.</summary>
    private bool AssetCombo(string label, ref string path, AssetKind kind, bool dropdownStyle = false, bool allowEmpty = false)
    {
        bool changed = false;
        if (!dropdownStyle)
        {
            float button = ImGui.GetFrameHeight();
            float width = ImGui.CalcItemWidth();
            ImGui.SetNextItemWidth(width - button - ImGui.GetStyle().ItemInnerSpacing.X);
            string text = path;
            if (ImGui.InputText("##path" + label, ref text, 512))
            {
                string candidate = OsbParser.CleanPath(text);
                if (AssetPathPolicy.Allows(app.Project!, candidate))
                {
                    path = candidate;
                    changed = true;
                }
                else app.SetStatus(AssetPathPolicy.Message(candidate));
            }
            ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
        }
        var flags = ImGuiComboFlags.HeightLarge;
        if (!dropdownStyle) flags |= ImGuiComboFlags.NoPreview | ImGuiComboFlags.PopupAlignLeft;
        if (ImGui.BeginCombo(label, dropdownStyle ? path.Length == 0 ? L.T("common.none") : path : "", flags))
        {
            if (dropdownStyle)
            {
                bool appearing = ImGui.IsWindowAppearing();
                if (appearing) assetFilter = "";
                ImGui.SetNextItemWidth(-1);
                if (appearing) ImGui.SetKeyboardFocusHere();
                Theme.SearchInput("##assetFilter", L.T("props.assetFilter") + "…", ref assetFilter, 512);
                ImGui.Separator();
            }
            if (allowEmpty && ImGui.Selectable(L.T("common.none"), path.Length == 0))
            {
                changed = path.Length != 0;
                path = "";
            }
            string search = assetFilter.Replace('\\', '/');
            foreach (var asset in app.Assets)
            {
                if (asset.Kind != kind) continue;
                if (dropdownStyle && !asset.Path.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
                bool current = asset.Path.Equals(path, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(asset.Path, current))
                {
                    path = asset.Path;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        return changed;
    }
}
