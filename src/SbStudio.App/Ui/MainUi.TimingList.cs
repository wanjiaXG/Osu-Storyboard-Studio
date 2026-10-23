using System.Numerics;
using ImGuiNET;
using SbStudio.Core;

namespace SbStudio.App.Ui;

public sealed partial class MainUi
{
    private const int TimingRowsInView = 8;
    private int timingToShow = -1;
    private TimingListEdit? timingListEdit;
    private TimingPoint? timingListCurrent;
    private readonly TimingOffsetPopup timingOffsetPopup = new();

    // Red lines of the draft ticked for deleting together.
    private readonly HashSet<TimingPoint> timingListPicked = new(ReferenceEqualityComparer.Instance);

    private void DrawTimingListDialog(Vector2 centre)
    {
        if (!BeginModal(L.T("timing.list") + TimingListDialog, centre, 560))
        {
            timingListEdit = null;
            timingOffsetPopup.Reset();
            return;
        }
        var project = app.Project;
        if (project == null)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }
        if (timingListEdit == null || timingListEdit.Project != project || ImGui.IsWindowAppearing())
        {
            timingListEdit = new TimingListEdit(project);
            timingListPicked.Clear();
            timingOffsetPopup.Reset();
            // The red line whose BPM is in effect at the playhead (the first one also covers earlier times),
            // found in the draft by position so the highlight stays on it while the draft is edited.
            var playing = TimingUtil.At(project.Settings.Timing, app.Time);
            int index = playing == null ? -1 : project.Settings.Timing.IndexOf(playing);
            timingListCurrent = index >= 0 && index < timingListEdit.Timing.Count ? timingListEdit.Timing[index] : null;
            timingToShow = index;
        }
        var timing = timingListEdit.Timing;
        ImGui.SeparatorText(L.T("settings.timing"));
        Theme.TextDimmed(L.T("settings.timingNote"));
        // Adding, reading and copying red lines: at the top right, above the list.
        bool changed = false;
        var style = ImGui.GetStyle();
        float Width(string label) => ImGui.CalcTextSize(label.Split("###")[0]).X + style.FramePadding.X * 2;
        float actions = Width(L.T("settings.addSection")) + Width(L.T("settings.readOsu")) + Width(L.T("timing.copy")) + style.ItemSpacing.X * 2;
        // What the highlighted row means, in the space at the left of the buttons.
        float rowStartX = ImGui.GetCursorPosX(), rowWidth = ImGui.GetContentRegionAvail().X;
        {
            var swatchAt = ImGui.GetCursorScreenPos();
            float size = ImGui.GetTextLineHeight() * 0.8f, frame = ImGui.GetFrameHeight();
            ImGui.GetWindowDrawList().AddRectFilled(swatchAt + new Vector2(0, (frame - size) / 2), swatchAt + new Vector2(size, (frame + size) / 2),
                Theme.Rgba(235, 180, 80), Theme.Px(2));
            ImGui.Dummy(new Vector2(size, frame));
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            Theme.TextDimmed(L.T("timing.list.currentNote"));
            ImGui.SameLine();
        }
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), rowStartX + rowWidth - actions));
        if (ImGui.Button(L.T("settings.addSection")))
        {
            var added = new TimingPoint { Offset = app.SnappedTime, BeatLength = TimingUtil.At(timing, app.SnappedTime)?.BeatLength ?? timing.FirstOrDefault()?.BeatLength ?? 500 };
            timing.Add(added);
            timing.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            timingToShow = timing.IndexOf(added);
            changed = true;
        }
        ImGui.SameLine();
        if (ImGui.Button(L.T("settings.readOsu")))
        {
            var result = NativeFileDialogSharp.Dialog.FileOpen("osu", null);
            if (result.IsOk && !string.IsNullOrEmpty(result.Path)) ImportTimingListFile(result.Path);
        }
        ImGui.SameLine();
        // Copies the list as shown, unconfirmed edits included, in offset order.
        ImGui.BeginDisabled(timing.Count == 0);
        if (ImGui.Button(L.T("timing.copy") + "###copyTimingList"))
            timeline.CopyTiming(timing.OrderBy(p => p.Offset).ToList());
        ImGui.EndDisabled();
        Theme.Tooltip(L.T("timing.copy.tip"));

        changed |= TimingTable("timing", timing, TimingRowsInView, ref timingToShow, timingOffsetPopup, timingListCurrent, timingListPicked);
        changed |= timingOffsetPopup.Draw(app.Time);
        // Below the list, at the left: choosing rows and deleting them together.
        // Ticked rows: all or none at once, and deleted together (like any edit here, with OK and undoable).
        timingListPicked.RemoveWhere(p => !timing.Contains(p));
        ImGui.BeginDisabled(timing.Count == 0);
        bool all = timing.Count > 0 && timingListPicked.Count == timing.Count;
        if (ImGui.Button(L.T(all ? "timing.pickNone" : "timing.pickAll") + "###pickAllTiming"))
        {
            if (all) timingListPicked.Clear();
            else timingListPicked.UnionWith(timing);
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(timingListPicked.Count == 0);
        if (ImGui.Button(L.T("timing.deletePicked", timingListPicked.Count) + "###deletePickedTiming"))
        {
            timing.RemoveAll(timingListPicked.Contains);
            if (timingListCurrent != null && timingListPicked.Contains(timingListCurrent)) timingListCurrent = null;
            timingListPicked.Clear();
            changed = true;
        }
        ImGui.EndDisabled();
        Theme.Tooltip(L.T("timing.deletePicked.tip"));
        ImGui.SameLine(0, Theme.Px(12));
        ImGui.AlignTextToFramePadding();
        Theme.TextDimmed(L.T("settings.timingCount", timingListEdit.Timing.Count));
        // Retain the original table's ordering and drag behaviour.
        if (changed && !ImGui.IsAnyItemActive()) timing.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        ImGui.Spacing();
        ImGui.Separator();
        if (Theme.Button(L.T("common.ok"), 110))
        {
            app.ApplyTimingList(timingListEdit);
            timingListEdit = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (Theme.Button(L.T("common.cancel"), 110) || ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            timingListEdit = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    // The native picker and automated checks share this import path.
    internal void ImportTimingListFile(string path)
    {
        if (timingListEdit == null || timingListEdit.Project != app.Project) return;
        try
        {
            var osu = OsuFile.Read(path);
            timingOffsetPopup.Reset();
            timingListEdit.Timing = osu.Timing;
            timingToShow = 0;
            app.SetStatus(L.T("settings.readDone", osu.Timing.Count));
        }
        catch (Exception ex)
        {
            app.SetStatus(L.T("settings.readFailed", ex.Message));
        }
    }
}
