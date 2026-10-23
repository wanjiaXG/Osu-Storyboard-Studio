using System.Numerics;
using ImGuiNET;
using SbStudio.Core;

namespace SbStudio.App.Ui;

/// <summary>Ordered storyboard variables, with a numeric editor for otherwise invisible names.</summary>
public sealed class VariablesPanel(EditorApp app)
{
    public const string WindowId = "###variables";
    public bool Open = true;
    public uint DockBeside;
    public bool FocusRequested;
    private StoryboardVariable? original;
    private Project? editingProject;
    private string name = "", value = "", codes = "", error = "";
    private string? scope;
    private bool ascii, openEditor, openAscii, focusName, focusCodes, suggest;
    private Vector2 nameMin, nameMax;
    private const string EditorId = "###variableEditor";
    private const string AsciiId = "###asciiVariableName";

    public void Draw()
    {
        if (!Open) return;
        ImGui.SetNextWindowSize(new(Theme.Px(440), Theme.Px(320)), ImGuiCond.FirstUseEver);
        if (DockBeside != 0) ImGui.SetNextWindowDockID(DockBeside, ImGuiCond.FirstUseEver);
        if (FocusRequested) { ImGui.SetNextWindowFocus(); FocusRequested = false; }
        bool visible = ImGui.Begin(L.T("panel.variables") + WindowId, ref Open, ImGuiWindowFlags.NoFocusOnAppearing);
        if (visible && app.Project != null)
        {
            if (scope != null && scope != app.Project.Settings.Difficulty) scope = null;
            ImGui.BeginDisabled(!app.CanNavigateHistory);
            if (UiScriptTargets.Item("variables.add", ImGui.Button(L.T("common.add")))) Edit(null);
            if (app.Project.CurrentDifficulty is { } difficulty)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(MathF.Max(Theme.Px(80), ImGui.GetContentRegionAvail().X));
                if (ImGui.BeginCombo("##variableScope", scope == null ? L.T("variables.shared") : difficulty.DisplayName))
                {
                    if (ImGui.Selectable(L.T("variables.shared"), scope == null)) scope = null;
                    if (ImGui.Selectable(difficulty.DisplayName, scope != null)) scope = difficulty.File;
                    ImGui.EndCombo();
                }
            }
            ImGui.Separator();
            var listed = app.Doc.Variables.Where(v => v.Difficulty == scope).ToArray();
            if (listed.Length == 0) Theme.TextWrapped(L.T("variables.empty"));
            else if (ImGui.BeginTable("##variables", 3, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable))
            {
                ImGui.TableSetupColumn(L.T("variables.name"), ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn(L.T("variables.value"), ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed,
                    ImGui.CalcTextSize(L.T("variables.edit")).X + ImGui.CalcTextSize("X").X + ImGui.GetStyle().FramePadding.X * 4 + ImGui.GetStyle().ItemSpacing.X + Theme.Px(8));
                ImGui.TableHeadersRow();
                for (int i = 0; i < listed.Length; i++)
                {
                    var variable = listed[i];
                    ImGui.PushID(i);
                    ImGui.TableNextRow(); ImGui.TableNextColumn();
                    ImGui.TextUnformatted(StoryboardVariables.DisplayName(variable));
                    Theme.Tooltip(StoryboardVariables.DisplayName(variable));
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(SafeValue(variable.Value));
                    Theme.Tooltip(SafeValue(variable.Value));
                    ImGui.TableNextColumn();
                    if (UiScriptTargets.Item("variables.edit." + i, ImGui.SmallButton(L.T("variables.edit")))) Edit(variable);
                    ImGui.SameLine();
                    if (UiScriptTargets.Item("variables.delete." + i, ImGui.SmallButton("X##remove")))
                        error = app.ChangeVariable(variable, null) ?? "";
                    Theme.Tooltip(L.T("variables.delete"));
                    ImGui.PopID();
                }
                ImGui.EndTable();
            }
            ImGui.EndDisabled();
            if (error.Length > 0 && !ImGui.IsPopupOpen(EditorId)) Error(error);
        }
        DrawEditor();
        ImGui.End();
    }

    private static string SafeValue(string text) => text.Replace("\0", "\\0").Replace("\t", "\\t").Replace("\v", "\\v").Replace("\f", "\\f");

    private void Edit(StoryboardVariable? variable)
    {
        original = variable; editingProject = app.Project;
        ascii = variable != null && StoryboardVariables.UsesCodes(variable);
        name = variable == null ? "" : ascii ? "ASCII: " + StoryboardVariables.Codes(variable.Name[1..]) : variable.Name[1..];
        value = variable?.Value ?? "";
        error = ""; suggest = false; openEditor = focusName = true;
    }

    private void DrawEditor()
    {
        string title = (original == null ? L.T("variables.add") : L.T("variables.edit")) + EditorId;
        if (openEditor) { ImGui.OpenPopup(title); openEditor = false; }
        ModalSize(560);
        if (!ImGui.BeginPopupModal(title, ImGuiWindowFlags.AlwaysAutoResize)) return;
        if (ImGui.BeginTable("##fields", 2, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextRow(); ImGui.TableNextColumn();
            ImGui.TextUnformatted(L.T("variables.name"));
            ImGui.SetNextItemWidth(-1);
            if (focusName) { ImGui.SetKeyboardFocusHere(); focusName = false; }
            UiScriptTargets.Item("variables.name", ImGui.InputText("##variableName", ref name, 16384));
            bool active = ImGui.IsItemActive();
            nameMin = ImGui.GetItemRectMin(); nameMax = ImGui.GetItemRectMax();
            if (active) suggest = true;
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(L.T("variables.value"));
            ImGui.SetNextItemWidth(-1);
            UiScriptTargets.Item("variables.value", ImGui.InputText("##variableValue", ref value, (uint)Math.Max(65536, System.Text.Encoding.UTF8.GetByteCount(value) * 2 + 4096)));
            if (ImGui.IsItemActive()) suggest = false;
            ImGui.EndTable();
        }
        ImGui.Dummy(new(0, ImGui.GetFrameHeight() * (ascii ? 2 : 1) + Theme.Px(8)));
        string actualName = name;
        bool valid = ascii ? StoryboardVariables.TryAscii(name.StartsWith("ASCII:", StringComparison.Ordinal) ? name[6..] : name, out actualName)
            : StoryboardVariables.ValidName(actualName);
        bool available = app.CanNavigateHistory && editingProject == app.Project;
        if (name.Length > 0 && !valid) Error(ascii ? L.T("variables.invalidAscii") : L.T("variables.invalidName"));
        if (error.Length > 0) Error(error);
        if (!available) Error(L.T("variables.unavailable"));
        ImGui.BeginDisabled(!valid || !available);
        if (UiScriptTargets.Item("variables.confirm", ImGui.Button(L.T("common.confirm"))))
        {
            error = app.ChangeVariable(original, new StoryboardVariable("$" + actualName, value, original?.Difficulty ?? scope, ascii)) ?? "";
            if (error.Length == 0) { suggest = false; ImGui.CloseCurrentPopup(); }
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (UiScriptTargets.Item("variables.cancel", ImGui.Button(L.T("common.cancel"))) || ImGui.IsKeyPressed(ImGuiKey.Escape) && !ImGui.IsPopupOpen(AsciiId))
        { suggest = false; error = ""; ImGui.CloseCurrentPopup(); }
        DrawSuggestion();
        DrawAscii();
        ImGui.EndPopup();
    }

    private void DrawSuggestion()
    {
        if (!suggest) return;
        Vector2 cursor = ImGui.GetCursorScreenPos();
        float padding = Theme.Px(4);
        float width = MathF.Max(nameMax.X - nameMin.X, ImGui.CalcTextSize(L.T("variables.useAscii")).X + padding * 4);
        ImGui.SetCursorScreenPos(new(nameMin.X, nameMax.Y + Theme.Px(2)));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(padding));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, ImGui.GetStyle().Colors[(int)ImGuiCol.PopupBg]);
        if (ImGui.BeginChild("##variableNameSuggestion", new(width, ImGui.GetFrameHeight() * (ascii ? 2 : 1) + padding * 2),
            ImGuiChildFlags.Border | ImGuiChildFlags.AlwaysUseWindowPadding,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNavFocus))
        {
            if (UiScriptTargets.Item("variables.useAscii", ImGui.Button(L.T("variables.useAscii"), new(width - padding * 2, 0))))
            {
                codes = ascii ? name.StartsWith("ASCII:", StringComparison.Ordinal) ? name[6..].Trim() : name : StoryboardVariables.Codes(name);
                suggest = false; openAscii = focusCodes = true;
            }
            if (ascii && UiScriptTargets.Item("variables.useText", ImGui.Button(L.T("variables.useText"), new(width - padding * 2, 0))))
            { ascii = false; name = ""; suggest = false; focusName = true; }
            if (!ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem) && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) suggest = false;
        }
        ImGui.EndChild(); ImGui.PopStyleColor(); ImGui.PopStyleVar();
        ImGui.SetCursorScreenPos(cursor);
    }

    private void DrawAscii()
    {
        string title = L.T("variables.asciiTitle") + AsciiId;
        if (openAscii) { ImGui.OpenPopup(title); openAscii = false; }
        ModalSize(480);
        if (!ImGui.BeginPopupModal(title, ImGuiWindowFlags.AlwaysAutoResize)) return;
        Theme.TextWrapped(L.T("variables.asciiHint"));
        if (focusCodes) { ImGui.SetKeyboardFocusHere(); focusCodes = false; }
        ImGui.SetNextItemWidth(-1);
        UiScriptTargets.Item("variables.codes", ImGui.InputText("##asciiCodes", ref codes, 16384));
        bool valid = StoryboardVariables.TryAscii(codes, out string actual);
        if (valid) Theme.TextWrapped("ASCII: " + StoryboardVariables.Codes(actual));
        else if (codes.Length > 0) Error(L.T("variables.invalidAscii"));
        ImGui.BeginDisabled(!valid);
        if (UiScriptTargets.Item("variables.asciiConfirm", ImGui.Button(L.T("common.confirm"))))
        { ascii = true; name = "ASCII: " + StoryboardVariables.Codes(actual); error = ""; ImGui.CloseCurrentPopup(); }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (UiScriptTargets.Item("variables.asciiCancel", ImGui.Button(L.T("common.cancel"))) || ImGui.IsKeyPressed(ImGuiKey.Escape)) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private static void Error(string message)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError); Theme.TextWrapped(message); ImGui.PopStyleColor();
    }

    private static void ModalSize(float width)
    {
        var viewport = ImGui.GetWindowViewport();
        var maximum = Vector2.Max(new(Theme.Px(200), Theme.Px(180)), viewport.WorkSize - new Vector2(Theme.Px(20)));
        float actualWidth = MathF.Min(Theme.Px(width), maximum.X);
        ImGui.SetNextWindowSizeConstraints(new(actualWidth, 0), maximum);
        ImGui.SetNextWindowPos(viewport.WorkPos + viewport.WorkSize / 2, ImGuiCond.Appearing, new(0.5f));
    }
}
