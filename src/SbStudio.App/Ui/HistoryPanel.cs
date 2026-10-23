using System.Numerics;
using System.Globalization;
using ImGuiNET;
using SbStudio.Core;

namespace SbStudio.App.Ui;

/// <summary>Clickable session history, sharing the editor's existing undo and redo states.</summary>
public sealed unsafe class HistoryPanel(EditorApp app)
{
    public const string WindowId = "###history";
    public bool Open = true;
    public uint DockBeside;
    private int seenRevision = -1;
    private readonly HoverPopup limitPopup = new();
    private float limitDraft;
    private string limitText = "";
    private bool limitDragging, limitCorrected;

    public void Draw()
    {
        if (!Open) return;
        ImGui.SetNextWindowSize(new Vector2(Theme.Px(320), Theme.Px(360)), ImGuiCond.FirstUseEver);
        if (DockBeside != 0) ImGui.SetNextWindowDockID(DockBeside, ImGuiCond.FirstUseEver);
        if (!ImGui.Begin(L.T("panel.history") + WindowId, ref Open, ImGuiWindowFlags.NoFocusOnAppearing))
        {
            ImGui.End();
            return;
        }

        ImGui.BeginDisabled(!app.CanNavigateHistory);
        ImGui.BeginDisabled(!app.CanUndo);
        if (UiScriptTargets.Item("history.undo", ImGui.Button(L.T("menu.edit.undo")))) app.Undo();
        Theme.Tooltip("Ctrl+Z");
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!app.CanRedo);
        if (UiScriptTargets.Item("history.redo", ImGui.Button(L.T("menu.edit.redo")))) app.Redo();
        Theme.Tooltip("Ctrl+Y / Ctrl+Shift+Z");
        ImGui.EndDisabled();
        ImGui.SameLine();
        Theme.TextDimmed(L.T("history.position", app.HistoryPosition, Math.Max(0, app.HistoryCount - 1)));
        ImGui.Separator();
        Theme.TextWrapped(L.T("history.hint"));
        Theme.Tooltip(L.T("history.branch"));
        ImGui.Spacing();

        bool reveal = seenRevision != app.HistoryRevision;
        seenRevision = app.HistoryRevision;
        if (ImGui.BeginChild("##states", new Vector2(0, -ImGui.GetFrameHeightWithSpacing() - Theme.Px(4))))
        {
            float rowHeight = Theme.Px(26) + ImGui.GetStyle().ItemSpacing.Y;
            if (reveal) ImGui.SetScrollY(MathF.Max(0, app.HistoryPosition * rowHeight - (ImGui.GetWindowHeight() - rowHeight) / 2));
            var entries = app.HistoryEntries;
            var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
            clipper.Begin(entries.Count, rowHeight);
            while (clipper.Step())
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var entry = entries[i];
                ImGui.PushID(entry.Index);
                if (entry.Future) ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
                bool opening = entry.Index == 0 && entry.Label == L.T("history.initial");
                string label = (entry.Current ? "> " : "  ") +
                    (opening ? "" : entry.Index.ToString("D2") + "  ") + entry.Label;
                if (UiScriptTargets.Item("history." + entry.Index, ImGui.Selectable(label, entry.Current, ImGuiSelectableFlags.None, new Vector2(0, Theme.Px(26)))))
                {
                    app.GoToHistory(entry.Index);
                    seenRevision = -1;
                }
                string tip = entry.Current ? L.T("history.current") : entry.Future ? L.T("history.redoTo") : L.T("history.undoTo");
                Theme.Tooltip(entry.Label + "\n" + tip);
                if (entry.Future) ImGui.PopStyleColor();
                ImGui.PopID();
            }
            clipper.End();
            clipper.Destroy();
        }
        ImGui.EndChild();
        DrawHistoryLimit();
        ImGui.EndDisabled();
        ImGui.End();
    }

    private void DrawHistoryLimit()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(L.T("history.limit"));
        ImGui.SameLine();
        ImGui.Button(app.HistoryLimit.ToString(CultureInfo.InvariantCulture) + "###historyLimit", new Vector2(Theme.Px(76), 0));
        Theme.Tooltip(L.T("history.session", app.HistoryLimit));
        if (!limitPopup.Open)
        {
            limitDraft = app.HistoryLimit;
            limitText = app.HistoryLimit.ToString(CultureInfo.InvariantCulture);
            limitDragging = limitCorrected = false;
        }
        HoverPopup.Draw("##historyLimitPopup", limitPopup, 360, 5, false, DrawLimitControls);
    }

    private bool DrawLimitControls()
    {
        ImGui.TextUnformatted(L.T("history.limit"));
        float inputWidth = Theme.Px(80);
        if (ProgressSlider.Range("##limitSlider", ref limitDraft, AppSettings.MinHistoryLimit, AppSettings.MaxHistoryLimit,
            MathF.Max(1, ImGui.GetContentRegionAvail().X - inputWidth - ImGui.GetStyle().ItemSpacing.X)))
        {
            limitText = ((int)limitDraft).ToString(CultureInfo.InvariantCulture);
            limitCorrected = false;
        }
        if (ImGui.IsItemActive()) limitDragging = true;
        if (ImGui.IsItemDeactivated() && limitDragging)
        {
            app.SetHistoryLimit((int)limitDraft);
            limitDragging = false;
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(inputWidth);
        bool enter = ImGui.InputText("##limitInput", ref limitText, 32,
            ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
        if (enter || ImGui.IsItemDeactivatedAfterEdit())
        {
            bool parsed = long.TryParse(limitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value);
            limitCorrected = !parsed || value < AppSettings.MinHistoryLimit || value > AppSettings.MaxHistoryLimit;
            if (parsed) app.SetHistoryLimit((int)Math.Clamp(value, AppSettings.MinHistoryLimit, AppSettings.MaxHistoryLimit));
            limitDraft = app.HistoryLimit;
            limitText = app.HistoryLimit.ToString(CultureInfo.InvariantCulture);
        }
        if (limitCorrected)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError);
            Theme.TextWrapped(L.T("history.limit.validation", AppSettings.MinHistoryLimit, AppSettings.MaxHistoryLimit));
            ImGui.PopStyleColor();
        }
        ImGui.Spacing();
        Theme.TextWrapped(L.T("history.limit.memory"));
        return false;
    }
}
