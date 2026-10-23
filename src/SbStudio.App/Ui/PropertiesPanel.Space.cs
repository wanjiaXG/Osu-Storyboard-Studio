using System.Numerics;
using ImGuiNET;
using SbStudio.Core;

namespace SbStudio.App.Ui;

/// <summary>The space of an outermost group and its camera (see <see cref="GroupSpace"/>).</summary>
public sealed partial class PropertiesPanel
{
    /// <summary>
    /// The space section of a group. Only an outermost group has a space of its own; a group inside one
    /// says whose it is in, and offers to hand over a camera it kept from when it was outermost.
    /// </summary>
    private void DrawSpace(SbObjectGroup group)
    {
        if (group.Parent != null)
        {
            ImGui.TextWrapped(L.T("space.owner", group.Root.Name));
            if (group.Camera != null)
            {
                Theme.TextDimmed(L.T("camera.inactive"));
                CameraMoveButton(group);
            }
            return;
        }

        ImGui.SeparatorText(L.T("space.title"));
        if (group.Camera == null)
        {
            if (ImGui.Button(L.T("camera.add"))) app.AddCamera(group);
        }
        else
        {
            if (ImGui.Button(L.T("camera.title"))) app.SelectCamera(group);
            ImGui.SameLine();
            if (ImGui.Button(L.T("camera.remove"))) app.RemoveCamera(group);
        }

        // The settings are edited as a whole and only taken when they can be, so that an entry half typed
        // never leaves a near plane behind the camera or fog that ends before it starts.
        double near = group.NearClip, fade = group.NearFade, from = group.FogStart, to = group.FogEnd;
        bool changed = LabeledPair(L.T("space.near"), ref near, ref fade, PositionSpec);
        int fog = (int)group.Fog;
        changed |= ImGui.Combo(L.T("space.far"), ref fog, new[] { L.T("space.none"), L.T("space.fade"), L.T("space.darken") }, 3);
        changed |= LabeledPair(L.T("space.range"), ref from, ref to, PositionSpec);
        if (changed)
        {
            if (GroupSpace.ValidSpace(near, fade, from, to))
            {
                group.NearClip = near;
                group.NearFade = fade;
                group.FogStart = from;
                group.FogEnd = to;
                group.Fog = (FogMode)fog;
                app.TouchGroup(group);
            }
            else
            {
                app.SetStatus(L.T("parse.spaceValue"));
            }
        }

        // What the space costs in the exported file.
        var members = app.Doc.SpritesIn(group).Where(s => s.Timeline.HasCommands).ToArray();
        Theme.TextDimmed(L.T("space.statistics", members.Length, members.Sum(s => s.ExportedCommands()), members.Length));
    }

    /// <summary>The button that hands a kept camera to the outermost group, when that has none yet.</summary>
    private void CameraMoveButton(SbObjectGroup group)
    {
        Theme.Tooltip(L.T("camera.onlyRoot"));
        ImGui.BeginDisabled(group.Root.Camera != null);
        if (ImGui.Button(L.T("camera.move"))) app.MoveCameraToRoot(group);
        ImGui.EndDisabled();
    }

    /// <summary>The selected camera: its values at the playhead, where it is and how wide it sees, and its keyframes.</summary>
    private void DrawCamera(SbObjectGroup group)
    {
        ImGui.SeparatorText(group.CameraActive ? L.T("camera.title") : L.T("camera.inactive"));
        if (!group.CameraActive)
        {
            ImGui.TextWrapped(L.T("camera.onlyRoot"));
            CameraMoveButton(group);
            return;
        }

        var camera = group.Camera!;
        var state = camera.Evaluate(app.Time);
        foreach (var kind in SbInfo.CameraKinds)
            ImGui.TextUnformatted(Theme.KindLabel(kind) + ": " + TimelineValueInput.Display(state.Values(kind), kind, app.Settings.RotationInDegrees));
        ImGui.TextWrapped(L.T("camera.position", state.Position.X, state.Position.Y, state.Position.Z));
        // The screen is 640 wide: half of it seen at the zoom's distance.
        ImGui.TextUnformatted(L.T("camera.view", 2 * Math.Atan(320 / state.Zoom) * 180 / Math.PI));
        DegreesToggle();
        foreach (var kind in SbInfo.CameraKinds)
        {
            if (ImGui.SmallButton(Theme.KindLabel(kind)))
            {
                app.AddCameraKey(group, kind);
                CommandAdded?.Invoke();
            }
        }
        if (DrawTracks(camera.Commands, 0, true, null, app.Settings.FilterCommands ? app.ActiveTrack?.Kind : null, true)) app.TouchGroup(group);
    }
}
