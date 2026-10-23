using ImGuiNET;
using SbStudio.Core;

namespace SbStudio.App.Ui;

public sealed partial class PropertiesPanel
{
    /// <summary>The time span a trigger accepts events in, as the header and notes show it.</summary>
    private static string TriggerWindowText(SbTrigger t) => (t.Start == int.MinValue, t.End == int.MaxValue) switch
    {
        (true, true) => L.T("props.triggerWholeTime"),
        (false, true) => L.T("props.triggerFrom", Theme.FormatTime(t.Start)),
        (true, false) => L.T("props.triggerUntil", Theme.FormatTime(t.End)),
        _ => Theme.FormatTime(t.Start) + " – " + Theme.FormatTime(t.End),
    };

    private bool DrawTrigger(SbSprite sprite, SbTrigger trigger, ref bool changed)
    {
        bool open = GroupHeader(trigger, L.T("props.triggerHeader", trigger.Name, TriggerWindowText(trigger)) + "###trigger", out bool remove);
        if (!open) return remove;

        changed |= TriggerConditionPicker.Draw(app, ref trigger.Name);
        changed |= DrawTriggerWindow(sprite, trigger);
        DrawTriggerSimulation(sprite, trigger);
        DrawTriggerTimeNote(sprite, trigger);

        DrawGroupBody(sprite, trigger, ref changed);
        ImGui.TreePop();
        return remove;
    }

    /// <summary>When the trigger accepts events, each end of it possibly without limit, and its group number.</summary>
    private bool DrawTriggerWindow(SbSprite sprite, SbTrigger trigger)
    {
        bool changed = false;
        ImGui.SeparatorText(L.T("props.triggerWindow"));
        string startLabel = L.T("props.triggerStart"), endLabel = L.T("props.triggerEnd"), unbounded = L.T("props.triggerUnbounded");
        float label = MathF.Max(MathF.Max(ImGui.CalcTextSize(startLabel).X, ImGui.CalcTextSize(endLabel).X), ImGui.CalcTextSize(L.T("props.triggerGroup")).X) + ImGui.GetStyle().ItemSpacing.X;

        bool End(bool isStart)
        {
            ImGui.PushID(isStart ? "start" : "end");
            float x0 = ImGui.GetCursorPosX();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(isStart ? startLabel : endLabel);
            ImGui.SameLine(x0 + label);
            bool open = isStart ? trigger.Start == int.MinValue : trigger.End == int.MaxValue;
            bool result = false;
            if (ImGui.Checkbox(unbounded, ref open))
            {
                int now = app.SnappedTime;
                if (isStart) trigger.Start = open ? int.MinValue : trigger.End == int.MaxValue ? now : Math.Min(now, trigger.End);
                else trigger.End = open ? int.MaxValue : trigger.Start == int.MinValue ? now : Math.Max(now, trigger.Start);
                result = true;
            }
            Theme.Tooltip(L.T(isStart ? "props.triggerStartTime" : "props.triggerEndTime"));
            if (!open)
            {
                ImGui.SameLine();
                float width = ImGui.GetContentRegionAvail().X;
                result |= isStart
                    ? TimeField("##time", ref trigger.Start, width, 0, int.MinValue + 1, trigger.End, L.T("props.triggerStartTime"))
                    : TimeField("##time", ref trigger.End, width, 0, trigger.Start, int.MaxValue - 1, L.T("props.triggerEndTime"));
            }
            ImGui.PopID();
            return result;
        }
        changed |= End(true);
        changed |= End(false);

        float groupX = ImGui.GetCursorPosX();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(L.T("props.triggerGroup"));
        ImGui.SameLine(groupX + label);
        changed |= NumberInput.Field(app, "##group", ref trigger.Group, Theme.Px(70), new NumberSpec(L.T("props.triggerGroupName"), 0.1f, "%.0f", 0, int.MaxValue));
        ImGui.SameLine();
        Theme.TextDimmed(L.T("props.triggerGroupUnused"));

        // What osu!lazer does with it that is easy to overlook.
        if (trigger.Start == 0 && trigger.End == 0) Warning(L.T("props.triggerZeroWindow"));
        if (!sprite.Timeline.HasCommands) Warning(L.T("props.triggerOnlySprite"));
        return changed;
    }

    private static void Warning(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextWarning);
        Theme.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    /// <summary>How the trigger fares in the simulation: which firing the playhead is at, and the rules that make it fire.</summary>
    private void DrawTriggerSimulation(SbSprite sprite, SbTrigger trigger)
    {
        ImGui.SeparatorText(L.T("props.triggerSimulation"));
        var anchor = app.TriggerAnchor(trigger, sprite);
        if (app.RenderOptions.Simulation is { } simulation)
        {
            var fires = simulation.FiresOf(sprite, trigger);
            if (anchor.Simulated)
            {
                Theme.TextWrapped(L.T("props.triggerFiring", anchor.Index + 1, anchor.Count, Theme.FormatTime(anchor.Time)));
                int previous = anchor.Index - 1;
                int next = fires[anchor.Index].Time > app.Time ? anchor.Index : anchor.Index + 1;
                ImGui.BeginDisabled(previous < 0);
                if (ImGui.SmallButton(L.T("props.triggerPrevFire")) && previous >= 0) app.Seek(fires[previous].Time);
                ImGui.EndDisabled();
                ImGui.SameLine();
                ImGui.BeginDisabled(next >= fires.Count);
                if (ImGui.SmallButton(L.T("props.triggerNextFire")) && next < fires.Count) app.Seek(fires[next].Time);
                ImGui.EndDisabled();
                ImGui.SameLine();
                if (ImGui.SmallButton(L.T("props.triggerThisFire"))) app.Seek(anchor.Time);

                var rules = app.Project!.Settings.TriggerSimulationRules;
                var names = fires.Select(f => simulation.Stream.Events[f.EventIndex].RuleIndex).Distinct()
                    .Where(i => i >= 0 && i < rules.Count).Select(i => RuleName(rules[i])).ToArray();
                Theme.TextWrapped(names.Length > 0 ? L.T("props.triggerRules", string.Join(L.T("props.triggerRuleSeparator"), names)) : L.T("props.triggerNoRules"));
            }
            else
            {
                var report = app.SimulationReports.FirstOrDefault(r => r.Sprite == sprite && r.Trigger == trigger);
                Theme.TextWrapped(report is { Notes.Count: > 0 } ? ViewerPanel.SimulationNoteText(report, report.Notes[0]) : L.T("props.triggerNoRules"));
            }
        }
        else Theme.TextDimmed(L.T("props.triggerNotSimulated"));

        if (ImGui.SmallButton(L.T("simulation.detail.addMatching")) && TriggerCondition.TryParse(trigger.Name, out var condition))
            app.RequestSimulationRule(sprite, trigger, condition);
        ImGui.SameLine();
        if (ImGui.SmallButton(L.T("simulation.inspector.open"))) app.ShowSimulationTrigger(sprite, trigger);
    }

    private static string RuleName(TriggerSimulationRule rule) => rule.Name.Length > 0 ? rule.Name : rule.Kind switch
    {
        TriggerRuleKind.Beatmap => L.T("simulation.kind.beatmap"),
        TriggerRuleKind.State => "Pass/Fail",
        TriggerRuleKind.HitObjectHit => "HitObjectHit",
        _ => "HitSound",
    };

    /// <summary>The line above a trigger's keyframes: what their times count from.</summary>
    private void DrawTriggerTimeNote(SbSprite sprite, SbTrigger trigger)
    {
        var anchor = app.TriggerAnchor(trigger, sprite);
        Theme.TextDimmed(anchor.Simulated
            ? L.T("props.triggerRelative", Theme.FormatTime(anchor.Time))
            : L.T("props.triggerRelativeNone", trigger.Start == int.MinValue ? Theme.FormatTime(0) : Theme.FormatTime(trigger.Start)));
    }
}
