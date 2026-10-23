using System.Numerics;
using ImGuiNET;
using SbStudio.Core;

namespace SbStudio.App.Ui;

public sealed partial class TimelinePanel
{
    private bool optionsExpanded;

    private int OptionsLineCount => 4 + (app.Project!.CurrentDifficulty != null ? 1 : 0)
        + (app.Settings.ShowHitSoundRecheck || app.Settings.ShowTimingRecheck ? 1 : 0)
        + (app.OpenGroup == null ? 1 : 0);

    private float TimelineHeaderHeight(float available, float rowHeight, float scrollHeight)
    {
        float compact = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y + Theme.Px(22)
            + (app.OpenGroup != null ? Theme.Px(18) : 0);
        float desired = compact + (optionsExpanded ? OptionsLineCount * Theme.Px(18) + ImGui.GetStyle().ItemSpacing.Y : 0);
        // Keep the fold button reachable in short panels; its contents can scroll when necessary.
        return MathF.Max(compact, MathF.Min(desired, available - scrollHeight - rowHeight));
    }

    private void DrawHeaderOptions(Vector2 origin, float headerHeight)
    {
        var dl = ImGui.GetWindowDrawList();
        ImGui.SetCursorScreenPos(origin + new Vector2(Theme.Px(8), ImGui.GetStyle().ItemSpacing.Y / 2));
        ImGui.SetNextItemWidth(MathF.Max(1, leftWidth - Theme.Px(16)));
        UiScriptTargets.Item("timeline.search", Theme.SearchInput("##filter", L.T("timeline.filterHint"), ref filter, 96));
        float top = origin.Y + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y;
        float left = origin.X + Theme.Px(8);
        if (app.OpenGroup != null)
        {
            DrawPath(left, top, Theme.Px(18));
            top += Theme.Px(18);
        }

        float height = Theme.Px(22);
        var pos = new Vector2(origin.X, top);
        var max = new Vector2(trackX, top + height);
        bool drawOptions = optionsExpanded;
        ImGui.SetCursorScreenPos(pos);
        if (UiScriptTargets.Item("timeline.options", ImGui.InvisibleButton("##timelineOptions", new(leftWidth, height))) && !splitterDrag)
            optionsExpanded = !optionsExpanded;
        dl.AddRectFilled(pos, max, Theme.Rgba(48, 48, 52));
        dl.AddRectFilled(pos, new Vector2(pos.X + 3, max.Y), Theme.Rgba(170, 130, 230));
        Theme.FoldArrow(dl, pos + new Vector2(14, height / 2), 9, optionsExpanded, Theme.TextDim);
        dl.AddText(pos + new Vector2(26, (height - ImGui.GetFontSize()) / 2), ImGui.GetColorU32(ImGuiCol.Text), L.T("timeline.options"));
        if (!drawOptions) return;

        top += height;
        ImGui.SetCursorScreenPos(new Vector2(origin.X, top));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.BeginChild("##timelineOptionControls", new Vector2(leftWidth, MathF.Max(1, origin.Y + headerHeight - top)),
            ImGuiChildFlags.None, ImGuiWindowFlags.NoSavedSettings);
        ImGui.PopStyleVar();
        top = ImGui.GetCursorScreenPos().Y;
        float lineStep = Theme.Px(18);
        bool own = app.Project!.CurrentDifficulty != null;
        bool showFeedback = app.Settings.ShowHitSoundRecheck || app.Settings.ShowTimingRecheck;
        if (app.OpenGroup == null)
        {
            string objects = rowsShowingOnly ? L.T("timeline.objectsShown", rowsObjects, app.Doc.Objects.Count) : L.T("timeline.objects", app.Doc.Objects.Count);
            ImGui.SetCursorScreenPos(new Vector2(left, top));
            Theme.TextDimmed(objects);
            top += lineStep;
        }
        HeaderCheckbox(L.T("timeline.showingOnly"), ref showingOnly, left, top, lineStep, L.T("timeline.showingOnly.tip"), "timeline.option.showingOnly");
        top += lineStep;
        if (own)
        {
            HeaderCheckbox(L.T("timeline.ownOnly"), ref ownOnly, left, top, lineStep, L.T("timeline.ownOnly.tip"), "timeline.option.ownOnly");
            top += lineStep;
        }
        bool thumbnails = app.Settings.ShowTimelineThumbnails;
        HeaderCheckbox(L.T("timeline.showThumbnails"), ref thumbnails, left, top, lineStep, L.T("timeline.showThumbnails.tip"), "timeline.option.thumbnails");
        if (thumbnails != app.Settings.ShowTimelineThumbnails)
        {
            app.Settings.ShowTimelineThumbnails = thumbnails;
            app.Settings.Save();
        }
        top += lineStep;
        bool recheck = app.Settings.ShowHitSoundRecheck;
        HeaderCheckbox(L.T("hitsound.recheck"), ref recheck, left, top, lineStep, L.T("hitsound.recheck.tip"), "timeline.option.hitsound");
        if (recheck != app.Settings.ShowHitSoundRecheck)
        {
            app.SetHitSoundRecheck(recheck);
            app.Settings.Save();
            rowsStale = true;
        }
        top += lineStep;
        bool timingRecheck = app.Settings.ShowTimingRecheck;
        HeaderCheckbox(L.T("timing.recheck"), ref timingRecheck, left, top, lineStep, L.T("timing.recheck.tip"), "timeline.option.timing");
        if (timingRecheck != app.Settings.ShowTimingRecheck)
        {
            app.SetTimingRecheck(timingRecheck);
            app.Settings.Save();
            rowsStale = true;
        }
        if (showFeedback)
        {
            top += lineStep;
            bool timing = app.Settings.ShowTimingRecheck;
            bool brightnessFeedback = timing ? app.Settings.TimingBrightnessFeedback : app.Settings.HitSoundBrightnessFeedback;
            HeaderCheckbox(L.T(timing ? "timing.brightnessFeedback" : "hitsound.brightnessFeedback"), ref brightnessFeedback,
                left, top, lineStep, L.T(timing ? "timing.brightnessFeedback.tip" : "hitsound.brightnessFeedback.tip"), "timeline.option.feedback");
            if (brightnessFeedback != (timing ? app.Settings.TimingBrightnessFeedback : app.Settings.HitSoundBrightnessFeedback))
            {
                if (timing) app.Settings.TimingBrightnessFeedback = brightnessFeedback;
                else app.Settings.HitSoundBrightnessFeedback = brightnessFeedback;
                app.Settings.Save();
            }
        }
        ImGui.Dummy(new Vector2(1, 1));
        ImGui.EndChild();
    }
}
