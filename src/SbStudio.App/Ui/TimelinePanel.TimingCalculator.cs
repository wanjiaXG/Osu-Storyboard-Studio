using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SbStudio.Core;

namespace SbStudio.App.Ui;

public sealed partial class TimelinePanel
{
    private const string TimingCalculatorId = "###timingCalculatorPopup";
    private bool requestTimingCalculator;
    private Project? timingCalculatorProject;
    private string timingCalculatorTarget = "0";
    private int timingCalculatorSteps = 1, timingCalculatorDivisor = 4, timingCalculatorDecimals = 3;
    // The BPM field follows the suggestion until it is typed in; changing the inputs makes it follow again.
    private string timingCalculatorBpm = "";
    private bool timingCalculatorBpmEdited;
    private TimingBpmDirection timingCalculatorDirection;
    private (string, int, int, int, TimingBpmDirection) timingCalculatorInputs;

    internal void OpenTimingCalculator()
    {
        timingCalculatorProject = app.Project;
        timingCalculatorTarget = TimingPointEditing.CurrentOffset(app.Time).ToString(CultureInfo.InvariantCulture);
        timingCalculatorDivisor = app.Divisor;
        EstimateTimingCalculatorSteps();
        timingCalculatorBpmEdited = false;
        requestTimingCalculator = true;
    }

    private void EstimateTimingCalculatorSteps()
    {
        if (app.Project != null && int.TryParse(timingCalculatorTarget, out int target))
            timingCalculatorSteps = TimingBpmCalculator.EstimateSteps(app.Project.Settings.Timing, target, timingCalculatorDivisor, timingCalculatorDirection);
    }

    private static void DrawCalculatorIcon(ImDrawListPtr dl, Vector2 centre, float size, uint colour)
    {
        float unit = size / 22;
        Vector2 P(float x, float y) => centre + new Vector2(x, y) * unit;
        dl.AddRect(P(-6, -8), P(6, 8), colour, unit * 1.5f, ImDrawFlags.None, unit * 1.2f);
        dl.AddRect(P(-4, -6), P(4, -2), colour, unit * 0.5f);
        for (int row = 0; row < 2; row++)
            for (int column = 0; column < 3; column++)
                dl.AddRectFilled(P(-4 + column * 3, 1 + row * 4), P(-2.5f + column * 3, 3 + row * 4), colour, unit * 0.3f);
    }

    private void DrawTimingCalculatorPopup()
    {
        string title = L.T("timing.calculateBpm") + TimingCalculatorId;
        if (requestTimingCalculator)
        {
            requestTimingCalculator = false;
            ImGui.OpenPopup(title);
        }
        var viewport = ImGui.GetMainViewport();
        if (!MainUi.BeginModal(title, viewport.Pos + viewport.Size / 2, 460)) return;
        if (app.Project != timingCalculatorProject)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }
        if (UiScriptTargets.Item("calculator.previous", ImGui.RadioButton(L.T("timing.calculatorDirectionPrevious"), timingCalculatorDirection == TimingBpmDirection.Previous)))
        {
            timingCalculatorDirection = TimingBpmDirection.Previous;
            EstimateTimingCalculatorSteps();
        }
        ImGui.SameLine();
        if (UiScriptTargets.Item("calculator.next", ImGui.RadioButton(L.T("timing.calculatorDirectionNext"), timingCalculatorDirection == TimingBpmDirection.Next)))
        {
            timingCalculatorDirection = TimingBpmDirection.Next;
            EstimateTimingCalculatorSteps();
        }
        bool next = timingCalculatorDirection == TimingBpmDirection.Next;
        ImGui.TextWrapped(next ? L.T("timing.calculatorNextNote") : L.T("timing.calculatorNote"));
        ImGui.SetNextItemWidth(Theme.Px(190));
        if (UiScriptTargets.Item("calculator.target", ImGui.InputText(L.T("viewer.currentTime") + " (ms)", ref timingCalculatorTarget, 32))) EstimateTimingCalculatorSteps();
        if (UiScriptTargets.Item("calculator.current", ImGui.Button(L.T("timing.calculatorUseCurrent"))))
        {
            timingCalculatorTarget = TimingPointEditing.CurrentOffset(app.Time).ToString(CultureInfo.InvariantCulture);
            EstimateTimingCalculatorSteps();
        }
        bool validTarget = int.TryParse(timingCalculatorTarget.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int target);
        var points = app.Project!.Settings.Timing;
        var reference = validTarget ? TimingBpmCalculator.Reference(points, target, timingCalculatorDirection) : null;
        if (reference != null) ImGui.TextUnformatted(next ? L.T("timing.calculatorNext", reference.Offset, reference.Bpm)
            : L.T("timing.calculatorPrevious", reference.Offset, reference.Bpm));
        ImGui.SetNextItemWidth(Theme.Px(100));
        if (ImGui.BeginCombo(L.T("timeline.snap"), $"1/{timingCalculatorDivisor}"))
        {
            foreach (int divisor in TimingUtil.Divisors)
            {
                if (!ImGui.Selectable($"1/{divisor}", timingCalculatorDivisor == divisor)) continue;
                double beats = (double)timingCalculatorSteps / timingCalculatorDivisor;
                timingCalculatorDivisor = divisor;
                timingCalculatorSteps = (int)Math.Clamp(Math.Round(beats * divisor), 1, int.MaxValue);
            }
            ImGui.EndCombo();
        }
        ImGui.SetNextItemWidth(Theme.Px(190));
        UiScriptTargets.Position("calculator.steps", ImGui.GetCursorScreenPos() + new Vector2(Theme.Px(60), ImGui.GetFrameHeight() / 2));
        ImGui.InputInt(L.T("timing.calculatorSteps"), ref timingCalculatorSteps);
        if (reference != null && timingCalculatorSteps > 0)
        {
            double beats = (double)timingCalculatorSteps / timingCalculatorDivisor;
            int meter = TimingBpmCalculator.StartPoint(points, target, reference, timingCalculatorDirection).Meter;
            ImGui.TextUnformatted(L.T("timing.calculatorBeats", beats, beats / Math.Max(1, meter), meter));
        }
        ImGui.Separator();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(L.T("timing.analysis.bpmStep"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Theme.Px(100));
        if (ImGui.BeginCombo("##calculatorBpmDecimals", BpmDecimalsLabel(timingCalculatorDecimals)))
        {
            for (int decimals = 0; decimals <= 3; decimals++)
                if (ImGui.Selectable(BpmDecimalsLabel(decimals), decimals == timingCalculatorDecimals)) timingCalculatorDecimals = decimals;
            ImGui.EndCombo();
        }
        var suggestion = validTarget ? TimingBpmCalculator.Calculate(points, target, timingCalculatorSteps, timingCalculatorDivisor, timingCalculatorDecimals, timingCalculatorDirection) : null;
        var inputs = (timingCalculatorTarget, timingCalculatorSteps, timingCalculatorDivisor, timingCalculatorDecimals, timingCalculatorDirection);
        if (inputs != timingCalculatorInputs)
        {
            timingCalculatorInputs = inputs;
            timingCalculatorBpmEdited = false;
        }
        if (!timingCalculatorBpmEdited)
            timingCalculatorBpm = suggestion?.Bpm.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
        string? bpmError = null;
        TimingBpmResult? result = null;
        if (suggestion != null)
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(L.T("timing.calculatorBpm"));
            ImGui.SameLine();
            ImGui.SetNextItemWidth(Theme.Px(120));
            if (ImGui.InputText("##calculatorBpm", ref timingCalculatorBpm, 32, ImGuiInputTextFlags.CharsDecimal)) timingCalculatorBpmEdited = true;
            Theme.Tooltip(L.T("timing.calculatorBpm.tip", suggestion.Bpm));
            if (timingCalculatorBpmEdited && timingCalculatorBpm.Trim() != suggestion.Bpm.ToString("0.###", CultureInfo.InvariantCulture))
            {
                ImGui.SameLine();
                if (ImGui.Button(L.T("timing.calculatorUseSuggestion", suggestion.Bpm))) timingCalculatorBpmEdited = false;
            }
            bpmError = TimingPointEditing.ValidateBpmInput(timingCalculatorBpm, out double bpm);
            if (bpmError == null) result = TimingBpmCalculator.Evaluate(points, target, timingCalculatorSteps, timingCalculatorDivisor, bpm, timingCalculatorDirection);
            if (result != null)
            {
                // Off the target snap is allowed for a hand-tuned value, but said plainly.
                if (!result.Aligned) ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextWarning);
                ImGui.TextUnformatted(L.T("timing.calculatorError", result.ErrorMs));
                if (!result.Aligned) ImGui.PopStyleColor();
            }
        }
        string? error = !validTarget ? L.T("timing.invalidOffset") : reference == null ? (next ? L.T("timing.calculatorNoNext") : L.T("timing.calculatorNoPrevious"))
            : timingCalculatorSteps <= 0 ? L.T("timing.calculatorInvalidSteps") : suggestion == null ? L.T("timing.invalidBpm")
            : bpmError != null ? (bpmError == "timing.bpmPrecision" ? L.T("timing.bpmPrecision") : L.T("timing.invalidBpm")) : result == null ? L.T("timing.invalidBpm")
            : !suggestion.Aligned && !timingCalculatorBpmEdited ? L.T("timing.calculatorCannotAlign") : null;
        if (error != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError);
            ImGui.TextWrapped(error);
            ImGui.PopStyleColor();
        }
        ImGui.Separator();
        ImGui.BeginDisabled(error != null || result == null);
        if (UiScriptTargets.Item("calculator.apply", ImGui.Button(next ? L.T("timing.calculatorApplyNext") : L.T("timing.calculatorApply"))) && result != null && app.ApplyTimingBpm(result))
        {
            rowsStale = true;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (Theme.Button(L.T("common.cancel"), 100) || ImGui.IsKeyPressed(ImGuiKey.Escape)) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }
}
