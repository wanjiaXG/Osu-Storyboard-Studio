using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SbStudio.Core;

namespace SbStudio.App.Ui;

public sealed partial class TimelinePanel
{
    private SbSprite? timeSprite;
    private SbObjectGroup? timeGroup;
    private bool openTimeMenu, openTimeDialog;
    private int timeMode;
    private string timeStart = "", timeEnd = "", timeRate = "1", timeError = "";

    private void QueueClipTimeMenu(SbSprite? sprite, SbObjectGroup? group)
    {
        timeSprite = sprite; timeGroup = group;
        if (sprite != null) app.SelectOnly(sprite);
        else if (group != null) app.SelectGroup(group);
        app.ClearKeySelection(); app.ActiveTrack = null;
        openTimeMenu = true;
    }

    private void DrawClipTimeActions()
    {
        if (openTimeMenu) { ImGui.OpenPopup("##clipTimeMenu"); openTimeMenu = false; }
        if (ImGui.BeginPopup("##clipTimeMenu"))
        {
            bool valid = app.ClipTimeRange(timeSprite, timeGroup) is {} span && span.End > span.Start;
            if (UiScriptTargets.Item("clipTime.speed", ImGui.MenuItem(L.T("clipTime.speed"), null, false, valid)))
            {
                var range = app.ClipTimeRange(timeSprite, timeGroup)!.Value;
                timeStart = range.Start.ToString(CultureInfo.InvariantCulture);
                timeEnd = range.End.ToString(CultureInfo.InvariantCulture);
                timeRate = "1"; timeMode = 0; timeError = ""; openTimeDialog = true;
            }
            if (UiScriptTargets.Item("clipTime.reverse", ImGui.MenuItem(L.T("clipTime.reverse"), null, false, valid)))
            {
                var range = app.ClipTimeRange(timeSprite, timeGroup)!.Value;
                if (!app.TransformClipTime(timeSprite, timeGroup, range.Start, range.End, true, out var error))
                    app.ShowMessage(L.T("clipTime.reverse"), error!);
            }
            ImGui.EndPopup();
        }
        if (openTimeDialog) { ImGui.OpenPopup(L.T("clipTime.speed") + "##clipTimeDialog"); openTimeDialog = false; }
        ImGui.SetNextWindowSize(new Vector2(Theme.Px(430), 0), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal(L.T("clipTime.speed") + "##clipTimeDialog", ImGuiWindowFlags.AlwaysAutoResize)) return;
        if (UiScriptTargets.Item("clipTime.mode.range", ImGui.RadioButton(L.T("clipTime.byRange"), timeMode == 0))) { timeMode = 0; timeError = ""; }
        ImGui.SameLine();
        if (UiScriptTargets.Item("clipTime.mode.rate", ImGui.RadioButton(L.T("clipTime.byRate"), timeMode == 1))) { timeMode = 1; timeError = ""; }
        if (timeMode == 0)
        {
            ImGui.InputText(L.T("clipTime.start"), ref timeStart, 32); UiScriptTargets.Item("clipTime.start", false);
            ImGui.InputText(L.T("clipTime.end"), ref timeEnd, 32); UiScriptTargets.Item("clipTime.end", false);
        }
        else
        {
            ImGui.InputText(L.T("clipTime.rate"), ref timeRate, 32); UiScriptTargets.Item("clipTime.rate", false);
            Theme.TextWrapped(L.T("clipTime.rateTip"));
        }
        int start = 0, end = 0;
        bool validInput = false;
        if (app.ClipTimeRange(timeSprite, timeGroup) is {} original)
        {
            if (timeMode == 0)
                validInput = int.TryParse(timeStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out start) &&
                    int.TryParse(timeEnd, NumberStyles.Integer, CultureInfo.InvariantCulture, out end) && end > start;
            else if (double.TryParse(timeRate, NumberStyles.Float, CultureInfo.InvariantCulture, out double rate) && double.IsFinite(rate) && rate > 0)
            {
                double target = Math.Round(original.Start + ((double)original.End - original.Start) / rate);
                start = original.Start;
                if (double.IsFinite(target) && target < int.MaxValue && target > start) { end = (int)target; validInput = true; }
            }
        }
        if (!validInput) Theme.TextWrapped(L.T("clipTime.invalid"));
        if (timeError.Length > 0) Theme.TextWrapped(timeError);
        ImGui.BeginDisabled(!validInput);
        if (UiScriptTargets.Item("clipTime.apply", ImGui.Button(L.T("common.ok"))))
        {
            if (app.TransformClipTime(timeSprite, timeGroup, start, end, false, out var error)) ImGui.CloseCurrentPopup();
            else timeError = error!;
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (UiScriptTargets.Item("clipTime.cancel", ImGui.Button(L.T("common.cancel"))) || ImGui.IsKeyPressed(ImGuiKey.Escape)) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }
}
