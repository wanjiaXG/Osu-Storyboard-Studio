using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SbStudio.Core;

namespace SbStudio.App.Ui;

public sealed unsafe partial class ViewerPanel
{
    public event Action? SimulationLocated;
    private int simulationTriggerFilter, simulationEventFilter;
    private string simulationSearch = "";
    private bool simulationOnlyFiring, simulationOnlySelected, simulationFollow = true, simulationEventViewStale = true;
    private int[] simulationEventView = Array.Empty<int>(), simulationFireCounts = Array.Empty<int>();
    private double simulationFollowedTime = double.NaN;
    private SbTrigger? simulationEventSelected;

    internal static string SimulationProblem(string key) => key switch
    {
        "simulation.repeat.invalid" => L.T("simulation.repeat.invalid"),
        "simulation.interval.invalid" => L.T("simulation.interval.invalid"),
        "simulation.index.invalid" => L.T("simulation.index.invalid"),
        "simulation.range.invalid" => L.T("simulation.range.invalid"),
        _ => L.T("simulation.offset.invalid"),
    };
    internal static string SimulationStatus(TriggerStatus status) => status switch
    {
        TriggerStatus.Fires => L.T("simulation.status.fires"), TriggerStatus.FiresHidden => L.T("simulation.status.hidden"),
        TriggerStatus.Invalid => L.T("simulation.status.invalid"), _ => L.T("simulation.status.never"),
    };
    private static uint SimulationStatusColor(TriggerStatus status) => status switch
    {
        TriggerStatus.Fires => Theme.Rgba(70, 195, 130), TriggerStatus.FiresHidden => Theme.Rgba(230, 190, 65),
        TriggerStatus.Invalid => Theme.TextError, _ => Theme.Rgba(155, 155, 155),
    };
    internal static string SimulationWindow(SbTrigger t) => t.Start == int.MinValue && t.End == int.MaxValue ? L.T("simulation.window.unbounded")
        : (t.Start == int.MinValue ? "…" : Theme.FormatTime(t.Start)) + "–" + (t.End == int.MaxValue ? "…" : Theme.FormatTime(t.End));

    internal static string SimulationNoteText(TriggerReport report, (TriggerNote Note, string Detail) note, bool detail = false)
    {
        var t = report.Trigger;
        string[] values = note.Detail.Split('|');
        return note.Note switch
        {
            TriggerNote.InvalidName => detail ? L.T("simulation.note.invalidName.detail") : L.T("simulation.note.invalidName"),
            TriggerNote.NoMatchingEvent => detail ? L.T("simulation.note.noMatch.detail", t.Name) : L.T("simulation.note.noMatch"),
            TriggerNote.OutsideWindow => detail ? L.T("simulation.note.outsideWindow.detail", note.Detail + " ms", SimulationWindow(t)) : L.T("simulation.note.outsideWindow"),
            TriggerNote.FiresOutsideLifetime => detail ? L.T("simulation.note.outsideLifetime.detail") : L.T("simulation.note.outsideLifetime", note.Detail),
            TriggerNote.TriggerOnlySprite => detail ? L.T("simulation.note.triggerOnly.detail") : L.T("simulation.note.triggerOnly"),
            TriggerNote.CancelsNormalCommands => detail && values.Length == 3 ? L.T("simulation.note.cancels.detail", values[2] + " ms", values[1] + " ms",
                new[] { "MX", "MY", "S", "VX", "VY", "R", "CR", "CG", "CB", "F", "P H", "P V", "P A" }[int.Parse(values[0], CultureInfo.InvariantCulture)]) : L.T("simulation.note.cancels"),
            TriggerNote.GroupNumberUnused => detail ? L.T("simulation.note.group.detail") : L.T("simulation.note.group"),
            TriggerNote.ZeroWindow => detail ? L.T("simulation.note.zeroWindow.detail") : L.T("simulation.note.zeroWindow"),
            TriggerNote.HiddenLayer => detail ? L.T("simulation.note.hiddenLayer.detail", report.Sprite.Layer, note.Detail) : L.T("simulation.note.hiddenLayer"),
            _ => detail ? L.T("simulation.note.approximate.detail") : L.T("simulation.note.approximate"),
        };
    }

    private void LocateSimulationTrigger(TriggerReport report)
    {
        app.OpenContainerOf(report.Sprite); app.SelectOnly(report.Sprite); app.ClearKeySelection(); app.ActiveTrack = null;
        if(PluginLink.IsGenerated(report.Sprite)) {SimulationLocated?.Invoke();return;}
        report.Sprite.Expanded = true;
        app.SelectBlock(report.Sprite, report.Trigger); selectedSimulationReport = report; simulationEventViewStale = true;
        SimulationLocated?.Invoke();
    }

    internal static string SimulationExplain(string name)
    {
        if (!TriggerCondition.TryParse(name, out var c)) return L.T("simulation.note.invalidName.detail");
        if (c.Kind == TriggerKind.HitObjectHit) return L.T("simulation.explain.hit");
        if (c.Kind is TriggerKind.Passing or TriggerKind.Failing) return L.T("simulation.explain.state", c.Kind);
        var items = new List<string> { L.T("simulation.explain.any") };
        items.Add(L.T("simulation.explain.normalBank", c.NormalBank == TriggerSampleBank.Any ? L.T("simulation.explain.unbounded") : c.NormalBank.ToString()));
        items.Add(L.T("simulation.explain.additionBank", c.AdditionBank == TriggerSampleBank.Any ? L.T("simulation.explain.unbounded") : c.AdditionBank.ToString()));
        if (c.Addition != TriggerAddition.Any) items.Add(L.T("simulation.explain.addition", c.Addition));
        items.Add(L.T("simulation.explain.index", c.Suffix.Length == 0 ? L.T("simulation.explain.unbounded") : c.Suffix));
        return string.Join(" · ", items);
    }

    private void DrawSimulationTriggers()
    {
        if (SimulationPending) Theme.TextWrapped(L.T("simulation.draftBanner"));
        ImGui.SetNextItemWidth(Theme.Px(175));
        ImGui.Combo("##triggerFilter", ref simulationTriggerFilter, new[] { L.T("simulation.filter.all"), L.T("simulation.filter.problems"), L.T("simulation.filter.firing"), L.T("simulation.filter.selected") }, 4);
        ImGui.SameLine(); ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##triggerSearch", L.T("simulation.search"), ref simulationSearch, 512);
        var reports = simulationDraftReports.Where(r => (simulationSearch.Length == 0 || r.Sprite.Path.Contains(simulationSearch, StringComparison.OrdinalIgnoreCase))
            && (simulationTriggerFilter switch { 1 => r.Status != TriggerStatus.Fires || r.Notes.Count > 0, 2 => r.Fires.Count > 0,
                3 => app.IsSelected(r.Sprite), _ => true }))
            .OrderBy(r => r.Status == TriggerStatus.Fires && r.Notes.Count == 0 ? 1 : 0).ThenBy(r => r.Fires.FirstOrDefault()?.Time ?? double.PositiveInfinity).ToArray();
        float listHeight = MathF.Max(Theme.Px(100), ImGui.GetContentRegionAvail().Y * (selectedSimulationReport == null ? 0.96f : 0.48f));
        if (ImGui.BeginTable("##triggerReports", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable, new(0, listHeight)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn(L.T("simulation.col.status")); ImGui.TableSetupColumn(L.T("simulation.col.sprite"));
            ImGui.TableSetupColumn(L.T("simulation.col.condition")); ImGui.TableSetupColumn(L.T("simulation.col.window"));
            ImGui.TableSetupColumn(L.T("simulation.col.count")); ImGui.TableSetupColumn(L.T("simulation.col.next")); ImGui.TableSetupColumn(L.T("simulation.col.note"));
            ImGui.TableHeadersRow();
            foreach (var r in reports)
            {
                ImGui.PushID(r.Trigger.GetHashCode()); ImGui.TableNextRow(); ImGui.TableSetColumnIndex(0);
                var dot = ImGui.GetCursorScreenPos();
                ImGui.GetWindowDrawList().AddCircleFilled(dot + new Vector2(Theme.Px(4), ImGui.GetTextLineHeight() * 0.5f), Theme.Px(3), SimulationStatusColor(r.Status));
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.Px(12));
                ImGui.PushStyleColor(ImGuiCol.Text, SimulationStatusColor(r.Status));
                if (ImGui.Selectable(SimulationStatus(r.Status) + "###report", selectedSimulationReport?.Trigger == r.Trigger, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
                {
                    bool twice = ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left); LocateSimulationTrigger(r);
                    if (twice && r.Fires.Count > 0) app.Seek((r.Fires.FirstOrDefault(f => f.Time > app.Time) ?? r.Fires[0]).Time);
                }
                ImGui.PopStyleColor();
                Theme.Tooltip(string.Join("\n", r.Notes.Select(n => SimulationNoteText(r, n, true))));
                ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(Path.GetFileName(r.Sprite.Path));
                Theme.Tooltip(r.Sprite.Path + " · " + r.Sprite.Layer + " · " + (r.Shared ? L.T("simulation.shared") : L.T("simulation.difficulty"))
                    + (r.Sprite.Group is { } group ? " · " + group.Name : ""));
                Theme.TextDimmed((r.Shared ? L.T("simulation.shared") : L.T("simulation.difficulty")) + (r.Sprite.Group == null ? "" : " · " + r.Sprite.Group.Name));
                ImGui.TableSetColumnIndex(2);
                if (r.Status == TriggerStatus.Invalid) ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError);
                ImGui.TextUnformatted(r.Trigger.Name);
                if (r.Status == TriggerStatus.Invalid) ImGui.PopStyleColor();
                ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(SimulationWindow(r.Trigger));
                if (r.Trigger.Group != 0) { Theme.TextDimmed(L.T("simulation.window.group", r.Trigger.Group)); Theme.Tooltip(L.T("simulation.note.group.detail")); }
                ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted(r.Fires.Count.ToString());
                ImGui.TableSetColumnIndex(5); var next = r.Fires.FirstOrDefault(f => f.Time > app.Time);
                if (next != null) { if (ImGui.SmallButton(Theme.FormatTime(next.Time))) app.Seek(next.Time); } else Theme.TextDimmed("—");
                ImGui.TableSetColumnIndex(6); ImGui.TextUnformatted(r.Notes.Count > 0 ? SimulationNoteText(r, r.Notes[0]) : "—");
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        if (selectedSimulationReport is { } report) DrawSimulationTriggerDetail(report);
    }

    private void DrawSimulationTriggerDetail(TriggerReport report)
    {
        if (ImGui.BeginChild("##triggerDetail", new(0, 0)))
        {
            Theme.TextWrapped(report.Sprite.Path + " · " + report.Trigger.Name + " · " + SimulationWindow(report.Trigger));
            Theme.TextWrapped(SimulationExplain(report.Trigger.Name));
            foreach (var note in report.Notes) Theme.TextWrapped(SimulationNoteText(report, note) + " · " + SimulationNoteText(report, note, true));
            if (ImGui.Button(L.T("simulation.detail.select"))) LocateSimulationTrigger(report);
            ImGui.SameLine();
            var previous = report.Fires.LastOrDefault(f => f.Time < app.Time); var next = report.Fires.FirstOrDefault(f => f.Time > app.Time);
            ImGui.BeginDisabled(previous == null);
            if (ImGui.Button(L.T("simulation.detail.prev")) && previous != null) app.Seek(previous.Time);
            ImGui.EndDisabled(); ImGui.SameLine(); ImGui.BeginDisabled(next == null);
            if (ImGui.Button(L.T("simulation.detail.next")) && next != null) app.Seek(next.Time);
            ImGui.EndDisabled();
            if (TriggerCondition.TryParse(report.Trigger.Name, out var c) && ImGui.Button(L.T("simulation.detail.addMatching")))
                AddSimulationRule(TriggerRuleKind.HitSound, c);
            Theme.TextDimmed(L.T("simulation.detail.fires", report.Fires.Count));
            foreach (var fire in report.Fires.Take(200))
            {
                var e = simulationDraftPreview.Stream.Events[fire.EventIndex];
                if (ImGui.Selectable(Theme.FormatTime(fire.Time) + " · " + SimulationEventOrigin(e)
                    + (fire.Alive ? "" : " · " + L.T("simulation.detail.dead")) + "###fire" + fire.EventIndex)) app.Seek(fire.Time);
            }
            if (report.Fires.Count > 200) Theme.TextDimmed(L.T("simulation.detail.more", 200));
        }
        ImGui.EndChild();
    }

    private string SimulationEventOrigin(SimEvent e) => e.ObjectIndex >= 0 ? L.T("simulation.event.object", e.ObjectIndex + 1, SimulationPart(e.Part))
        : e.RuleIndex >= 0 && e.RuleIndex < simulationDraft.Count ? SimulationName(simulationDraft[e.RuleIndex]) : "—";
    private static string SimulationPart(SimPart part) => part switch
    {
        SimPart.Circle => L.T("simulation.part.circle"), SimPart.SliderHead => L.T("simulation.part.sliderHead"),
        SimPart.SliderRepeat => L.T("simulation.part.sliderRepeat"), SimPart.SliderTail => L.T("simulation.part.sliderTail"),
        SimPart.SliderTick => L.T("simulation.part.sliderTick"), SimPart.SpinnerEnd => L.T("simulation.part.spinnerEnd"),
        SimPart.Note => L.T("simulation.part.note"), SimPart.HoldHead => L.T("simulation.part.holdHead"), SimPart.HoldTail => L.T("simulation.part.holdTail"),
        SimPart.Drum => L.T("simulation.part.drum"), SimPart.DrumRoll => L.T("simulation.part.drumRoll"), SimPart.Fruit => L.T("simulation.part.fruit"),
        SimPart.Droplet => L.T("simulation.part.droplet"), SimPart.TinyDroplet => L.T("simulation.part.tinyDroplet"), SimPart.Banana => L.T("simulation.part.banana"), _ => "—",
    };

    private void DrawSimulationEvents()
    {
        if (SimulationPending) Theme.TextWrapped(L.T("simulation.draftBanner"));
        ImGui.SetNextItemWidth(Theme.Px(170));
        if (ImGui.Combo("##eventKind", ref simulationEventFilter, new[] { L.T("simulation.kindFilter.all"), "HitSound", "HitObjectHit", "Pass/Fail" }, 4)) simulationEventViewStale = true;
        ImGui.SameLine(); if (ImGui.Checkbox(L.T("simulation.follow"), ref simulationFollow)) simulationFollowedTime = double.NaN;
        if (ImGui.Checkbox(L.T("simulation.onlyFiring"), ref simulationOnlyFiring)) simulationEventViewStale = true;
        ImGui.SameLine(); if (ImGui.Checkbox(L.T("simulation.onlySelected"), ref simulationOnlySelected)) simulationEventViewStale = true;
        var events = simulationDraftPreview.Stream.Events;
        var selected = selectedSimulationReport?.Trigger ?? app.SelectedBlock as SbTrigger;
        if (selected != simulationEventSelected) { simulationEventSelected = selected; simulationEventViewStale = true; }
        if (simulationEventViewStale)
        {
            simulationFireCounts = new int[events.Count];
            foreach (var r in simulationDraftReports) foreach (var f in r.Fires) simulationFireCounts[f.EventIndex]++;
            TriggerCondition.TryParse(selected?.Name ?? "", out var condition);
            simulationEventView = Enumerable.Range(0, events.Count).Where(i =>
                (simulationEventFilter == 0 || events[i].Kind == (simulationEventFilter switch { 1 => SimEventKind.HitSound, 2 => SimEventKind.HitObjectHit, _ => SimEventKind.StateChange }))
                && (!simulationOnlyFiring || simulationFireCounts[i] > 0)
                && (!simulationOnlySelected || condition != null && TriggerMatcher.Matches(condition, events[i]))).ToArray();
            simulationEventViewStale = false; simulationFollowedTime = double.NaN;
        }
        if (ImGui.BeginTable("##simulationEvents", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.Resizable, new(0, 0)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn(L.T("simulation.col.time")); ImGui.TableSetupColumn(L.T("simulation.col.rule")); ImGui.TableSetupColumn(L.T("simulation.col.kind"));
            ImGui.TableSetupColumn(L.T("simulation.col.samples")); ImGui.TableSetupColumn(L.T("simulation.col.fired")); ImGui.TableHeadersRow();
            float height = ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().CellPadding.Y * 2;
            int lo = 0, hi = simulationEventView.Length;
            while (lo < hi) { int m = (lo + hi) / 2; if (events[simulationEventView[m]].Time <= app.Time) lo = m + 1; else hi = m; }
            int current = lo - 1;
            if (simulationFollow && simulationFollowedTime != app.Time)
            { ImGui.SetScrollY(MathF.Max(0, current * height - ImGui.GetWindowHeight() * 0.4f)); simulationFollowedTime = app.Time; }
            var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
            clipper.Begin(simulationEventView.Length, height);
            while (clipper.Step()) for (int row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
            {
                int index = simulationEventView[row]; var e = events[index]; ImGui.PushID(index);
                ImGui.TableNextRow(ImGuiTableRowFlags.None, height); if (row == current) ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, Theme.AccentDim);
                ImGui.TableSetColumnIndex(0);
                if (ImGui.Selectable(Theme.FormatTime(e.Time) + "###event", false, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
                {
                    app.Seek(e.Time);
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) { selectedSimulationRule = e.RuleIndex; simulationTab = 0; }
                }
                Theme.Tooltip(e.Time.ToString("0.0", CultureInfo.InvariantCulture) + " ms");
                ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(SimulationEventOrigin(e));
                ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(e.Kind == SimEventKind.StateChange ? e.Failing ? "Failing" : "Passing" : e.Kind.ToString());
                ImGui.TableSetColumnIndex(3);
                string samples = e.Kind == SimEventKind.StateChange ? e.Effective ? L.T("simulation.event.switch", e.Failing ? "Failing" : "Passing")
                    : L.T("simulation.event.noChange", e.Failing ? "Failing" : "Passing")
                    : string.Join(" · ", e.Samples.Select(s => s.Bank.ToString().ToLowerInvariant() + "-" + s.Name + s.Suffix));
                if (!e.Effective) Theme.TextDimmed(samples); else ImGui.TextUnformatted(samples);
                ImGui.TableSetColumnIndex(4);
                if (simulationFireCounts[index] == 0) Theme.TextDimmed("0"); else ImGui.TextUnformatted(simulationFireCounts[index].ToString());
                ImGui.PopID();
            }
            clipper.End(); clipper.Destroy(); ImGui.EndTable();
        }
    }
}
