using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SbStudio.Core;

namespace SbStudio.App.Ui;

public sealed partial class MainUi
{
    private const string SoundOffsetDialog = "###soundOffset";
    private string soundOffsetText = "0";

    private void ShowSoundOffset()
    {
        soundOffsetText = app.Settings.SoundOffsetMs.ToString(CultureInfo.InvariantCulture);
        openPopup = SoundOffsetDialog;
    }

    private void DrawSoundOffsetDialog(Vector2 centre)
    {
        if (!BeginModal(L.T("menu.preferences.soundOffset") + SoundOffsetDialog, centre, 480)) return;
        ImGui.TextWrapped(L.T("audio.offset.tip"));
        ImGui.Spacing();
        DrawSoundOffsetStep(-1);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Theme.Px(160));
        UiScriptTargets.Item("soundOffset.value", ImGui.InputText("##soundOffsetValue", ref soundOffsetText, 32));
        ImGui.SameLine();
        DrawSoundOffsetStep(1);
        ImGui.SameLine();
        ImGui.TextUnformatted(L.T("audio.offset.value"));
        bool valid = int.TryParse(soundOffsetText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value);
        if (!valid) ImGui.TextWrapped(L.T("audio.offset.invalid"));
        ImGui.Spacing();
        ImGui.BeginDisabled(!valid);
        if (UiScriptTargets.Item("soundOffset.confirm", ImGui.Button(L.T("common.confirm"))))
        {
            app.SetSoundOffset(value);
            app.Settings.Save();
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (UiScriptTargets.Item("soundOffset.reset", ImGui.Button(L.T("audio.offset.reset")))) soundOffsetText = "0";
        ImGui.SameLine();
        if (UiScriptTargets.Item("soundOffset.cancel", ImGui.Button(L.T("common.cancel")))) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawSoundOffsetStep(int direction)
    {
        bool valid = int.TryParse(soundOffsetText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value);
        long next = (long)value + direction * (ImGui.GetIO().KeyShift ? 5 : 1);
        ImGui.BeginDisabled(!valid || next < int.MinValue || next > int.MaxValue);
        float size = ImGui.GetFrameHeight();
        if (UiScriptTargets.Item(direction < 0 ? "soundOffset.decrease" : "soundOffset.increase",
            ImGui.Button(direction < 0 ? "-##decreaseSoundOffset" : "+##increaseSoundOffset", new(size, size))))
            soundOffsetText = next.ToString(CultureInfo.InvariantCulture);
        ImGui.EndDisabled();
        Theme.Tooltip(direction < 0 ? L.T("timing.adjustOffset.tip", "-1", "-5") : L.T("timing.adjustOffset.tip", "+1", "+5"));
    }
}
