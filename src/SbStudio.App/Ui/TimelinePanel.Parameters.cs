using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SbStudio.Core;

namespace SbStudio.App.Ui;

public sealed partial class TimelinePanel
{
    private SbCommand? parameterEditCommand;
    private char parameterEditFlag;
    private string parameterStartText = "", parameterEndText = "";
    private static readonly Localized<string[]> ParameterNames = new(() => new[] { L.T("param.H"), L.T("param.V"), L.T("param.A") });

    private void AddPropertyRows(Row row)
    {
        rows.Add(row);
        if (row.Command != CommandKind.P || !app.ParametersExpanded(RowNodes(row))) return;
        foreach (char flag in "HVA")
        {
            var child = row;
            child.Parameter = flag;
            rows.Add(child);
        }
    }

    private static bool MatchesCommand(Row row, SbCommand command) => command.Kind == row.Command &&
        (row.Parameter == '\0' || char.ToUpperInvariant(command.Param) == row.Parameter);

    private bool IsParameterRowActive(Row row) => app.IsActiveTrack(RowNodes(row), row.Command) &&
        (row.Command != CommandKind.P || row.Parameter == app.ActiveTrack?.Parameter);

    private void AddParameter(Row row)
    {
        if (row.Obj is SbSprite sprite) app.AddParameter(sprite, row.Group, row.Parameter);
    }

    private void ParameterSpanTargets(Row row, int offset, float mid, float right)
    {
        var commands = ParameterTrack.Commands(RowNodes(row), row.Parameter);
        for (int i = 0; i < commands.Count; i++)
        {
            float start = X(offset + commands[i].Start), end = X(offset + commands[i].End);
            string target = RowKeyTarget(row) + ".span." + i;
            if (start >= trackX && start <= right) UiScriptTargets.Position(target + ".start", new(start, mid));
            if (end >= trackX && end <= right) UiScriptTargets.Position(target + ".end", new(end, mid));
            if (end >= trackX && start <= right)
                UiScriptTargets.Position(target + ".middle", new((Math.Max(start, trackX) + Math.Min(end, right)) / 2, mid));
        }
    }

    private void DrawParameterLabel(ImDrawListPtr dl, Row row, Vector2 pos, Vector2 max, float rowHeight, bool hovered, bool clicked)
    {
        var nodes = RowNodes(row);
        float indent = row.Kind == RowKind.Property ? 44 : 60;
        float textY = pos.Y + (rowHeight - ImGui.GetFontSize()) / 2;
        if (row.Parameter == '\0')
        {
            bool expanded = app.ParametersExpanded(nodes);
            if (app.IsActiveTrack(nodes, CommandKind.P)) dl.AddRectFilled(pos, max, Theme.AccentDim);
            Theme.FoldArrow(dl, new(pos.X + indent - 10, pos.Y + rowHeight / 2), Theme.Px(7), expanded, Theme.TextDim);
            dl.AddText(new(pos.X + indent, textY), Theme.Rgba(190, 190, 196), Theme.KindLabel(CommandKind.P));
            string value = CurrentValue((SbSprite)row.Obj!, CommandKind.P);
            dl.AddText(new(max.X - 10 - ImGui.CalcTextSize(value).X, textY), Theme.Accent, value);
            UiScriptTargets.Position(RowKeyTarget(row) + ".fold", new(pos.X + indent - 10, pos.Y + rowHeight / 2));
            UiScriptTargets.Position(RowKeyTarget(row) + ".value", new(max.X - 16, pos.Y + rowHeight / 2));
            bool arrow = ImGui.GetIO().MousePos.X < pos.X + indent;
            if (clicked && arrow || hovered && !arrow && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                app.ExpandParameters(nodes, !expanded);
                if (!expanded) pendingExpansion = row;
            }
            else if (clicked)
            {
                app.SelectOnly(row.Obj!);
                app.SelectTrack(nodes, CommandKind.P);
            }
            DrawCommandContextMenu(row, hovered);
            return;
        }

        if (IsParameterRowActive(row)) dl.AddRectFilled(pos, max, Theme.AccentDim);
        UiScriptTargets.Position(RowKeyTarget(row) + ".label", new(pos.X + indent + 40, pos.Y + rowHeight / 2));
        bool onButton = KeyButton(dl, row, max, rowHeight, hovered, out float room, out int? direction);
        dl.AddRectFilled(new(pos.X + indent + 6, pos.Y + 5), new(pos.X + indent + 9, max.Y - 5), Theme.KindColor(CommandKind.P));
        string name = ParameterNames.Value["HVA".IndexOf(row.Parameter)];
        dl.PushClipRect(pos, new(max.X - room - Theme.Px(30), max.Y), true);
        dl.AddText(new(pos.X + indent + 16, textY), Theme.Rgba(190, 190, 196), name);
        dl.PopClipRect();
        bool onValue = DrawParameterValue(dl, row, pos, max, rowHeight, room, Theme.Accent, hovered);
        if (clicked && !onValue)
        {
            app.SelectOnly(row.Obj!);
            if (onButton) { if (direction is { } step) UseKeyButton(row, step); }
            else if (IsParameterRowActive(row)) { app.ClearKeySelection(); app.ActiveTrack = null; }
            else app.SelectTrack(nodes, CommandKind.P, row.Parameter);
        }
        DrawCommandContextMenu(row, hovered);
    }

    /// <summary>Edits one interval of a fixed flag, without replacing or disabling the other lanes.</summary>
    private bool DrawParameterValue(ImDrawListPtr dl, Row row, Vector2 pos, Vector2 max, float rowHeight, float buttonRoom, uint color, bool rowHovered)
    {
        var nodes = RowNodes(row);
        var commands = ParameterTrack.Commands(nodes, row.Parameter);
        bool editing = valueEditNodes == nodes && valueEditKind == CommandKind.P && parameterEditFlag == row.Parameter;
        int offset = PlayheadOffset(row);
        bool allowed = !app.Playing && row.Obj is SbSprite sprite && !app.IsProtectedBeatmapBackground(sprite) && !PluginLink.IsGenerated(sprite);
        if (editing && (!allowed || offset != valueEditOffset || !commands.Contains(parameterEditCommand!)))
        {
            valueEditNodes = null;
            editing = false;
        }
        string value = CurrentValue((SbSprite)row.Obj!, CommandKind.P).Contains(row.Parameter) ? row.Parameter.ToString() : "—";
        float right = max.X - 10 - buttonRoom;
        float width = ImGui.CalcTextSize(value).X;
        Vector2 textPos = new(right - width, pos.Y + (rowHeight - ImGui.GetFontSize()) / 2);
        dl.AddText(textPos, color, value);
        UiScriptTargets.Position(RowKeyTarget(row) + ".value", new(right - MathF.Max(width, Theme.Px(12)) / 2, pos.Y + rowHeight / 2));
        bool over = rowHovered && ImGui.IsMouseHoveringRect(new(right - MathF.Max(width, Theme.Px(12)) - 4, pos.Y), new(right + 4, max.Y));
        if (over && allowed && commands.Count > 0 && !editing)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            int count = ImGui.GetIO().MouseClickedLastCount[0];
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) ||
                (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && count > 2 && count % 2 == 0))
            {
                parameterEditCommand = commands.FirstOrDefault(app.SelectedCommands.Contains) ??
                    KeyTrack.Deciding(commands, (int)Math.Round(app.Time) - offset) ?? commands[0];
                PickCommand(parameterEditCommand);
                app.CommitEdits();
                valueEditNodes = nodes;
                valueEditKind = CommandKind.P;
                parameterEditFlag = row.Parameter;
                valueEditTime = app.Time;
                valueEditOffset = offset;
                editing = true;
                ImGui.SetNextWindowPos(new(textPos.X, max.Y), ImGuiCond.Always);
                ImGui.OpenPopup("##parameterValue");
            }
        }
        if (editing) valueEditFrame = ImGui.GetFrameCount();
        if (ImGui.BeginPopup("##parameterValue"))
        {
            if (!editing) ImGui.CloseCurrentPopup();
            else
            {
                ImGui.TextUnformatted(ParameterNames.Value["HVA".IndexOf(row.Parameter)]);
                if (commands.Count > 1)
                {
                    ImGui.SetNextItemWidth(Theme.Px(220));
                    bool open = ImGui.BeginCombo("##parameterCommand", Caption(parameterEditCommand!));
                    UiScriptTargets.Item("timeline.parameter.command", open);
                    if (open)
                    {
                        for (int i = 0; i < commands.Count; i++)
                            if (UiScriptTargets.Item("timeline.parameter.command." + i,
                                ImGui.Selectable(Caption(commands[i]) + "##" + i, commands[i] == parameterEditCommand)))
                                PickCommand(commands[i]);
                        ImGui.EndCombo();
                    }
                }
                ImGui.SetNextItemWidth(Theme.Px(150));
                bool confirm = UiScriptTargets.Item("timeline.parameter.start", ImGui.InputText(L.T("props.caption.start"), ref parameterStartText, 32, ImGuiInputTextFlags.EnterReturnsTrue));
                ImGui.SetNextItemWidth(Theme.Px(150));
                confirm |= UiScriptTargets.Item("timeline.parameter.end", ImGui.InputText(L.T("props.caption.end"), ref parameterEndText, 32, ImGuiInputTextFlags.EnterReturnsTrue));
                bool startValid = int.TryParse(parameterStartText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int start);
                bool endValid = int.TryParse(parameterEndText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int end);
                bool valid = startValid && endValid && start <= end;
                if (!startValid || !endValid) Theme.TextWrapped(L.T("simulation.offset.invalid"));
                else if (start > end) Theme.TextWrapped(L.T("props.startTime"));
                else if (start == end) Theme.TextDimmed(L.T("props.paramWhole"));
                ImGui.BeginDisabled(!valid);
                confirm |= UiScriptTargets.Item("timeline.parameter.confirm", ImGui.Button(L.T("common.confirm")));
                ImGui.EndDisabled();
                if (valid && confirm)
                {
                    if (parameterEditCommand!.Start != start || parameterEditCommand.End != end)
                    {
                        parameterEditCommand.Start = start;
                        parameterEditCommand.End = end;
                        app.Touch(row.Obj);
                        app.CommitEdits();
                    }
                    valueEditNodes = null;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (UiScriptTargets.Item("timeline.parameter.cancel", ImGui.Button(L.T("common.cancel"))) || ImGui.IsKeyPressed(ImGuiKey.Escape))
                { valueEditNodes = null; ImGui.CloseCurrentPopup(); }
            }
            ImGui.EndPopup();
        }
        else if (editing) valueEditNodes = null;
        return over;

        void PickCommand(SbCommand command)
        {
            parameterEditCommand = command;
            parameterStartText = command.Start.ToString(CultureInfo.InvariantCulture);
            parameterEndText = command.End.ToString(CultureInfo.InvariantCulture);
            app.SelectOnly(row.Obj!);
            app.SelectTrack(nodes, CommandKind.P, row.Parameter);
            app.SelectedCommands.Add(command);
        }
        static string Caption(SbCommand command) => $"{command.Start} → {command.End}";
    }
}
