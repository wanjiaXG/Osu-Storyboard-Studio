using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SbStudio.Core;

namespace SbStudio.App.Ui;

public sealed unsafe partial class ViewerPanel
{
    private bool showTriggerSimulation, simulationCloseRequested, simulationAppliedChanged;
    private Project? simulationProject;
    private List<TriggerSimulationRule> simulationDraft = new(), simulationApplied = new();
    private readonly TriggerSimulation simulationDraftPreview = new();
    private IReadOnlyList<TriggerReport> simulationDraftReports = Array.Empty<TriggerReport>();
    private bool simulationDraftStale = true;
    private int simulationDraftRevision = -1, selectedSimulationRule = -1, simulationTab = -1;
    private OsuBeatmap? simulationDraftMap;
    private TriggerReport? selectedSimulationReport;
    private readonly Dictionary<string, string> simulationInputs = new(), simulationInputErrors = new();
    private bool SimulationPending => simulationInputErrors.Count > 0 || !EditorApp.SameSimulationRules(simulationDraft, simulationApplied);

    public object TriggerSimulationUiState => new { Open = showTriggerSimulation, Pending = SimulationPending, Draft = simulationDraft, Errors = simulationInputErrors.Count };

    public void ShowTriggerSimulation() { SyncSimulationDraft(); showTriggerSimulation = true; }

    private void OpenSimulationTrigger(SbSprite sprite, SbTrigger trigger)
    {
        ShowTriggerSimulation(); EnsureSimulationDraft(); simulationTab = 1;
        selectedSimulationReport = simulationDraftReports.FirstOrDefault(r => r.Sprite == sprite && r.Trigger == trigger);
    }

    /// <summary>A draft rule whose events meet the trigger's condition, at the playhead; Confirm applies it.</summary>
    private void AddMatchingSimulationRule(SbSprite sprite, SbTrigger trigger, TriggerCondition condition)
    {
        ShowTriggerSimulation();
        AddSimulationRule(TriggerRuleKind.HitSound, condition);
    }

    private void SyncSimulationDraft()
    {
        var project = app.Project;
        if (project == null) return;
        var applied = project.Settings.TriggerSimulationRules;
        if (simulationProject != project)
        {
            simulationProject = project; simulationApplied = applied.Select(r => r.Clone()).ToList(); RevertSimulation();
        }
        else if (!EditorApp.SameSimulationRules(applied, simulationApplied))
        {
            bool pending = SimulationPending;
            simulationApplied = applied.Select(r => r.Clone()).ToList();
            if (!pending) RevertSimulation(); else simulationAppliedChanged = true;
        }
    }

    private void RevertSimulation()
    {
        simulationDraft = simulationApplied.Select(r => r.Clone()).ToList();
        simulationInputs.Clear(); simulationInputErrors.Clear(); simulationAppliedChanged = false;
        simulationDraftStale = true; selectedSimulationRule = simulationDraft.Count > 0 ? 0 : -1;
    }

    private void EnsureSimulationDraft()
    {
        if (!simulationDraftStale && simulationDraftRevision == app.DocRevision && simulationDraftMap == app.Beatmap) return;
        simulationDraftMap = app.Beatmap; simulationDraftRevision = app.DocRevision; simulationDraftStale = false;
        simulationDraftPreview.Configure(TriggerEventStream.Build(simulationDraft, simulationDraftMap));
        var selected = selectedSimulationReport;
        simulationDraftReports = simulationDraftPreview.Reports(app.Doc);
        selectedSimulationReport = simulationDraftReports.FirstOrDefault(r => r.Sprite == selected?.Sprite && r.Trigger == selected?.Trigger);
        simulationEventViewStale = true;
    }

    private void ConfirmSimulation()
    {
        if (simulationInputErrors.Count > 0 || simulationDraft.Any(r => r.Problem() != null)) return;
        app.ApplySimulationRules(simulationDraft);
        simulationApplied = app.Project!.Settings.TriggerSimulationRules.Select(r => r.Clone()).ToList();
        simulationAppliedChanged = false;
    }

    private void DrawSimulationButton(Action<float> nextTool)
    {
        SyncSimulationDraft();
        int count = app.Project!.Settings.TriggerSimulationRules.Count(r => r.Enabled);
        string label = count > 0 ? L.T("simulation.button.on", count) : L.T("simulation.button.off");
        if (SimulationPending) label = L.T("simulation.button.pending", label);
        nextTool(Theme.ButtonWidth(label, 140));
        if (count > 0) ImGui.PushStyleColor(ImGuiCol.Button, Theme.Accent);
        if (ImGui.Button(label + "###triggerSimulationButton"))
        {
            if (showTriggerSimulation && SimulationPending) simulationCloseRequested = true;
            else showTriggerSimulation = !showTriggerSimulation;
        }
        if (count > 0) ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
        {
            var reports = app.SimulationReports;
            Theme.Tooltip(L.T("simulation.button.tip", count, app.TriggerSimulation.Stream.Events.Count, reports.Count,
                reports.Count(r => r.Status == TriggerStatus.Fires), reports.Count(r => r.Status == TriggerStatus.FiresHidden),
                reports.Count(r => r.Status == TriggerStatus.NeverFires), reports.Count(r => r.Status == TriggerStatus.Invalid)));
        }
    }

    private void DrawTriggerSimulation()
    {
        if (!showTriggerSimulation || app.Project == null) return;
        SyncSimulationDraft(); EnsureSimulationDraft();
        ImGui.SetNextWindowSizeConstraints(new(Theme.Px(680), Theme.Px(420)), new(float.MaxValue));
        ImGui.SetNextWindowSize(new(Theme.Px(960), Theme.Px(640)), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new(0.5f));
        bool open = true;
        if (ImGui.Begin(L.T("simulation.title") + "###triggerSimulation", ref open, ImGuiWindowFlags.NoDocking))
        {
            if (simulationAppliedChanged) Theme.TextWrapped(L.T("simulation.appliedChanged"));
            float footer = ImGui.GetFrameHeightWithSpacing() * 2;
            if (ImGui.BeginChild("##simulationBody", new(0, -footer)))
            {
                if (ImGui.BeginTabBar("##simulationTabs"))
                {
                    DrawSimulationTab(0, L.T("simulation.tab.rules", simulationDraft.Count), DrawSimulationRules);
                    DrawSimulationTab(1, L.T("simulation.tab.triggers", simulationDraftReports.Count), DrawSimulationTriggers);
                    DrawSimulationTab(2, L.T("simulation.tab.events", simulationDraftPreview.Stream.Events.Count), DrawSimulationEvents);
                    DrawSimulationTab(3, L.T("simulation.tab.mechanism"), DrawSimulationMechanism);
                    ImGui.EndTabBar();
                }
            }
            ImGui.EndChild();
            ImGui.Separator();
            Theme.TextDimmed(L.T("simulation.footer", simulationDraft.Count(r => r.Enabled)));
            if (SimulationPending) { ImGui.SameLine(); Theme.TextDimmed(L.T("simulation.pending")); }
            float buttons = Theme.ButtonWidth(L.T("simulation.revert"), 90) + Theme.ButtonWidth(L.T("simulation.confirm"), 90) + ImGui.GetStyle().ItemSpacing.X;
            float buttonStart = ImGui.GetWindowWidth() - ImGui.GetStyle().WindowPadding.X - buttons;
            if (ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X <= ImGui.GetWindowPos().X + buttonStart) ImGui.SameLine(buttonStart);
            if (Theme.Button(L.T("simulation.revert"), 90)) RevertSimulation();
            ImGui.SameLine();
            ImGui.BeginDisabled(!SimulationPending || simulationInputErrors.Count > 0 || simulationDraft.Any(r => r.Problem() != null));
            if (Theme.Button(L.T("simulation.confirm"), 90)) ConfirmSimulation();
            ImGui.EndDisabled(); Theme.Tooltip(L.T("simulation.confirm.tip"));
        }
        ImGui.End();
        if (!open) { if (SimulationPending) simulationCloseRequested = true; else showTriggerSimulation = false; }
        if (simulationCloseRequested) { ImGui.OpenPopup("###simulationClose"); simulationCloseRequested = false; }
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Always, new(0.5f));
        if (ImGui.BeginPopupModal(L.T("simulation.close.title") + "###simulationClose", ImGuiWindowFlags.AlwaysAutoResize))
        {
            Theme.TextWrapped(L.T("simulation.pending"));
            ImGui.BeginDisabled(simulationInputErrors.Count > 0 || simulationDraft.Any(r => r.Problem() != null));
            if (ImGui.Button(L.T("simulation.close.confirm"))) { ConfirmSimulation(); showTriggerSimulation = false; ImGui.CloseCurrentPopup(); }
            ImGui.EndDisabled(); ImGui.SameLine();
            if (ImGui.Button(L.T("simulation.close.discard"))) { RevertSimulation(); showTriggerSimulation = false; ImGui.CloseCurrentPopup(); }
            ImGui.SameLine();
            if (ImGui.Button(L.T("simulation.close.keep"))) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    private void DrawSimulationTab(int index, string label, Action draw)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(label + "###simulationTab" + index + "\0");
        bool visible;
        fixed (byte* text = bytes) visible = ImGuiNative.igBeginTabItem(text, null, simulationTab == index ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None) != 0;
        if (!visible) return;
        if (simulationTab == index) simulationTab = -1;
        draw(); ImGui.EndTabItem();
    }

    private static string SimulationKind(TriggerRuleKind kind) => kind switch
    {
        TriggerRuleKind.HitSound => "HitSound", TriggerRuleKind.HitObjectHit => "HitObjectHit", TriggerRuleKind.State => "Pass/Fail",
        TriggerRuleKind.Beatmap => L.T("simulation.kind.beatmap"), _ => "?",
    };
    private static string SimulationName(TriggerSimulationRule r) => r.Name.Length > 0 ? r.Name : SimulationKind(r.Kind);
    private string SimulationSummary(TriggerSimulationRule r)
    {
        if (r.Kind == TriggerRuleKind.Unknown) return L.T("simulation.legacyInvalid", r.Condition ?? "");
        if (r.Kind == TriggerRuleKind.Beatmap) return L.T("simulation.summary.beatmap",
            r.From == null && r.To == null ? L.T("simulation.summary.wholeSong") : $"{r.From?.ToString() ?? "…"}–{r.To?.ToString() ?? "…"} ms",
            r.BeatmapSounds && r.BeatmapHits ? L.T("simulation.summary.both") : r.BeatmapSounds ? "HitSound" : r.BeatmapHits ? "HitObjectHit" : "—");
        return r.Offset + " ms · " + (r.Kind == TriggerRuleKind.State ? r.Failing ? "Failing" : "Passing" : r.Kind == TriggerRuleKind.HitObjectHit ? "HitObjectHit"
            : string.Join(" · ", TriggerEventStream.SamplesOf(r).Select(s => s.Bank.ToString().ToLowerInvariant() + "-" + s.Name + s.Suffix)))
            + (r.Kind != TriggerRuleKind.State && r.RepeatCount > 1 ? " · " + L.T("simulation.summary.repeat", r.RepeatCount, r.RepeatInterval) : "");
    }

    private void AddSimulationRule(TriggerRuleKind kind, TriggerCondition? matching = null)
    {
        int time = (int)Math.Round(app.Time);
        var rule = matching != null ? TriggerSimulationRule.Matching(matching, time) : new TriggerSimulationRule { Kind = kind, Offset = time };
        if (matching == null && kind == TriggerRuleKind.HitSound && app.SelectedBlock is SbTrigger t
            && TriggerCondition.TryParse(t.Name, out var c) && c.Kind == TriggerKind.HitSound) rule = TriggerSimulationRule.Matching(c, time);
        simulationDraft.Add(rule); selectedSimulationRule = simulationDraft.Count - 1; simulationDraftStale = true; simulationTab = 0;
    }

    private void DrawSimulationRules()
    {
        float height = MathF.Max(100, ImGui.GetContentRegionAvail().Y - Theme.Px(55));
        float left = MathF.Max(Theme.Px(230), ImGui.GetContentRegionAvail().X * 0.42f);
        if (ImGui.BeginChild("##ruleList", new(left, height), ImGuiChildFlags.Border))
        {
            float available = ImGui.GetContentRegionAvail().X;
            int rows = SimulationToolRows(available, "+ " + L.T("simulation.add"), L.T("simulation.duplicate"), L.T("simulation.delete"))
                + SimulationToolRows(available, L.T("simulation.moveUp"), L.T("simulation.moveDown"))
                + SimulationToolRows(available, L.T("simulation.checkAll"), L.T("simulation.checkNone"));
            float controls = ImGui.GetFrameHeightWithSpacing() * rows;
            if (ImGui.BeginChild("##rules", new(0, -controls)))
                for (int i = 0; i < simulationDraft.Count; i++)
                {
                    var r = simulationDraft[i]; ImGui.PushID(i);
                    bool enabled = r.Enabled;
                    if (ImGui.Checkbox("##enabled", ref enabled)) { r.Enabled = enabled; simulationDraftStale = true; }
                    Theme.Tooltip(L.T("simulation.ruleEnabled")); ImGui.SameLine();
                    if (r.Kind == TriggerRuleKind.Unknown) ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError);
                    if (ImGui.Selectable(SimulationName(r) + "###rule", selectedSimulationRule == i, ImGuiSelectableFlags.AllowDoubleClick))
                    { selectedSimulationRule = i; if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && r.Kind != TriggerRuleKind.Beatmap) app.Seek(r.Offset); }
                    if (r.Kind == TriggerRuleKind.Unknown) ImGui.PopStyleColor();
                    Theme.Tooltip(SimulationSummary(r));
                    Theme.TextDimmed(SimulationSummary(r)); ImGui.PopID();
                }
            ImGui.EndChild();
            if (ImGui.Button("+ " + L.T("simulation.add"))) ImGui.OpenPopup("##addSimulation");
            if (ImGui.BeginPopup("##addSimulation"))
            {
                foreach (var kind in new[] { TriggerRuleKind.HitSound, TriggerRuleKind.HitObjectHit, TriggerRuleKind.State, TriggerRuleKind.Beatmap })
                    if (ImGui.MenuItem(SimulationKind(kind))) AddSimulationRule(kind);
                ImGui.EndPopup();
            }
            bool selected = selectedSimulationRule >= 0 && selectedSimulationRule < simulationDraft.Count;
            SimulationToolInline(L.T("simulation.duplicate")); ImGui.BeginDisabled(!selected);
            if (ImGui.Button(L.T("simulation.duplicate")) && selected)
            { simulationDraft.Insert(selectedSimulationRule + 1, simulationDraft[selectedSimulationRule].Clone()); selectedSimulationRule++; simulationDraftStale = true; }
            SimulationToolInline(L.T("simulation.delete"));
            if (ImGui.Button(L.T("simulation.delete")) && selected)
            { simulationDraft.RemoveAt(selectedSimulationRule); selectedSimulationRule = Math.Min(selectedSimulationRule, simulationDraft.Count - 1); simulationInputs.Clear(); simulationInputErrors.Clear(); simulationDraftStale = true; }
            ImGui.EndDisabled();
            ImGui.BeginDisabled(!selected || selectedSimulationRule == 0);
            if (ImGui.Button(L.T("simulation.moveUp"))) MoveSimulationRule(-1);
            ImGui.EndDisabled(); SimulationToolInline(L.T("simulation.moveDown"));
            ImGui.BeginDisabled(!selected || selectedSimulationRule == simulationDraft.Count - 1);
            if (ImGui.Button(L.T("simulation.moveDown"))) MoveSimulationRule(1);
            ImGui.EndDisabled();
            if (ImGui.Button(L.T("simulation.checkAll"))) { simulationDraft.ForEach(r => r.Enabled = true); simulationDraftStale = true; }
            SimulationToolInline(L.T("simulation.checkNone"));
            if (ImGui.Button(L.T("simulation.checkNone"))) { simulationDraft.ForEach(r => r.Enabled = false); simulationDraftStale = true; }
        }
        ImGui.EndChild(); ImGui.SameLine();
        if (ImGui.BeginChild("##ruleParameters", new(0, height), ImGuiChildFlags.Border)
            && selectedSimulationRule >= 0 && selectedSimulationRule < simulationDraft.Count)
            DrawSimulationParameters(simulationDraft[selectedSimulationRule]);
        ImGui.EndChild();
        DrawSimulationStateTrack();
    }

    private static int SimulationToolRows(float available, params string[] labels)
    {
        int rows = 1; float used = 0;
        foreach (string label in labels)
        {
            float width = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetStyle().ItemSpacing.X;
            if (used > 0 && used + width > available) { rows++; used = 0; }
            used += width;
        }
        return rows;
    }

    private static void SimulationToolInline(string label)
    {
        float width = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2;
        if (ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + width <= ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X)
            ImGui.SameLine();
    }

    private void MoveSimulationRule(int direction)
    {
        int next = selectedSimulationRule + direction;
        (simulationDraft[next], simulationDraft[selectedSimulationRule]) = (simulationDraft[selectedSimulationRule], simulationDraft[next]);
        selectedSimulationRule = next; simulationDraftStale = true;
    }

    private void SimulationInput(string id, string label, string value, Func<string, string?> apply)
    {
        string text = simulationInputs.GetValueOrDefault(id, value);
        ImGui.SetNextItemWidth(MathF.Max(Theme.Px(85), ImGui.GetContentRegionAvail().X - Theme.LabelWidth(0, label)));
        if (ImGui.InputText(label + "###" + id, ref text, 128))
        {
            simulationInputs[id] = text;
            string? error = apply(text);
            if (error == null) { simulationInputErrors.Remove(id); simulationDraftStale = true; }
            else simulationInputErrors[id] = error;
        }
        if (simulationInputErrors.TryGetValue(id, out string? problem))
        { ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError); Theme.TextWrapped(SimulationProblem(problem)); ImGui.PopStyleColor(); }
    }

    private void DrawSimulationParameters(TriggerSimulationRule r)
    {
        string key = r.GetHashCode().ToString(CultureInfo.InvariantCulture) + ":";
        string name = r.Name;
        ImGui.SetNextItemWidth(-Theme.Px(65));
        if (ImGui.InputText(L.T("simulation.name"), ref name, 512)) { r.Name = name; simulationDraftStale = true; }
        var kinds = new[] { TriggerRuleKind.HitSound, TriggerRuleKind.HitObjectHit, TriggerRuleKind.State, TriggerRuleKind.Beatmap, TriggerRuleKind.Unknown };
        int kind = Array.IndexOf(kinds, r.Kind);
        ImGui.SetNextItemWidth(-Theme.Px(65));
        if (ImGui.Combo(L.T("simulation.kind"), ref kind, kinds.Select(SimulationKind).ToArray(), kinds.Length))
        { r.Kind = kinds[kind]; if (r.Kind != TriggerRuleKind.Unknown) r.Condition = null; simulationInputs.Clear(); simulationInputErrors.Clear(); simulationDraftStale = true; }
        if (r.Kind == TriggerRuleKind.Unknown) { Theme.TextWrapped(L.T("simulation.legacyInvalid", r.Condition ?? "")); return; }
        if (r.Kind != TriggerRuleKind.Beatmap)
        {
            SimulationInput(key + "offset", L.T("simulation.offset"), r.Offset.ToString(CultureInfo.InvariantCulture), text => {
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return "simulation.offset.invalid"; r.Offset = v; return null;
            }); Theme.Tooltip(L.T("simulation.offset.tip"));
            if (ImGui.Button(L.T("simulation.current"))) { r.Offset = (int)Math.Round(app.Time); simulationInputs.Remove(key + "offset"); simulationInputErrors.Remove(key + "offset"); simulationDraftStale = true; }
            ImGui.SameLine(); if (ImGui.Button(L.T("simulation.seek"))) app.Seek(r.Offset);
        }
        if (r.Kind is TriggerRuleKind.HitSound or TriggerRuleKind.HitObjectHit)
        {
            SimulationInput(key + "repeat", L.T("simulation.repeat"), r.RepeatCount.ToString(), text => {
                if (!int.TryParse(text, out int v) || v < 1) return "simulation.repeat.invalid"; r.RepeatCount = v; return null;
            });
            if (r.RepeatCount > 1)
            {
                SimulationInput(key + "interval", L.T("simulation.interval"), r.RepeatInterval.ToString(CultureInfo.InvariantCulture), text => {
                    if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal v) || v <= 0 || decimal.Round(v, 3) != v) return "simulation.interval.invalid";
                    r.RepeatInterval = (double)v; return null;
                });
                if (ImGui.Button(L.T("simulation.interval.snap")))
                {
                    r.RepeatInterval = Math.Round((TimingUtil.At(app.Project!.Settings.Timing, app.Time)?.BeatLength ?? 500) / app.Project.Settings.BeatDivisor, 3);
                    simulationInputs.Remove(key + "interval"); simulationInputErrors.Remove(key + "interval"); simulationDraftStale = true;
                }
            }
        }
        if (r.Kind == TriggerRuleKind.HitSound)
        {
            int sample = (int)r.SampleKind;
            ImGui.SetNextItemWidth(-Theme.LabelWidth(90, L.T("simulation.sampleKind")));
            if (ImGui.Combo(L.T("simulation.sampleKind"), ref sample, new[] { L.T("simulation.sampleKind.hit"), L.T("simulation.sampleKind.tick"), L.T("simulation.sampleKind.spinner") }, 3))
            { r.SampleKind = (SimSampleKind)sample; simulationDraftStale = true; }
            Theme.Tooltip(L.T("simulation.sampleKind.tip"));
            int normal = (int)r.NormalBank - 1, addition = (int)r.AdditionBank - 1;
            ImGui.SetNextItemWidth(-Theme.LabelWidth(90, L.T("simulation.normalBank")));
            if (ImGui.Combo(L.T("simulation.normalBank"), ref normal, "Normal\0Soft\0Drum\0")) { r.NormalBank = (TriggerSampleBank)(normal + 1); simulationDraftStale = true; }
            if (r.SampleKind != SimSampleKind.SpinnerBonus)
            {
                ImGui.SetNextItemWidth(-Theme.LabelWidth(90, L.T("simulation.additionBank")));
                if (ImGui.Combo(L.T("simulation.additionBank"), ref addition, "Normal\0Soft\0Drum\0")) { r.AdditionBank = (TriggerSampleBank)(addition + 1); simulationDraftStale = true; }
                ImGui.TextUnformatted(L.T("simulation.additions"));
                bool w = r.Whistle, f = r.Finish, c = r.Clap;
                if (ImGui.Checkbox("Whistle", ref w)) { r.Whistle = w; simulationDraftStale = true; }
                ImGui.SameLine(); if (ImGui.Checkbox("Finish", ref f)) { r.Finish = f; simulationDraftStale = true; }
                ImGui.SameLine(); if (ImGui.Checkbox("Clap", ref c)) { r.Clap = c; simulationDraftStale = true; }
            }
            SimulationInput(key + "index", L.T("simulation.index"), r.Index.ToString(), text => {
                if (!int.TryParse(text, out int v) || v < 0) return "simulation.index.invalid"; r.Index = v; return null;
            }); Theme.Tooltip(L.T("simulation.index.tip"));
            if (r.SampleKind == SimSampleKind.Hit)
            {
                bool file = r.FileSample;
                if (ImGui.Checkbox(L.T("simulation.fileSample"), ref file)) { r.FileSample = file; simulationDraftStale = true; }
                Theme.Tooltip(L.T("simulation.fileSample.tip"));
            }
        }
        if (r.Kind == TriggerRuleKind.State)
        {
            int state = r.Failing ? 1 : 0;
            if (ImGui.Combo(L.T("simulation.state"), ref state, "Passing\0Failing\0")) { r.Failing = state == 1; simulationDraftStale = true; }
            Theme.Tooltip(L.T("simulation.state.tip"));
        }
        if (r.Kind == TriggerRuleKind.Beatmap)
        {
            SimulationInput(key + "from", L.T("simulation.from"), r.From?.ToString() ?? "", text => {
                int? value = null;
                if (!string.IsNullOrWhiteSpace(text)) { if (!int.TryParse(text, out int v)) return "simulation.offset.invalid"; value = v; }
                if (value > r.To) return "simulation.range.invalid"; r.From = value; return null;
            }); Theme.Tooltip(L.T("simulation.range.any"));
            SimulationInput(key + "to", L.T("simulation.to"), r.To?.ToString() ?? "", text => {
                int? value = null;
                if (!string.IsNullOrWhiteSpace(text)) { if (!int.TryParse(text, out int v)) return "simulation.offset.invalid"; value = v; }
                if (value < r.From) return "simulation.range.invalid"; r.To = value; return null;
            }); Theme.Tooltip(L.T("simulation.range.any"));
            bool sounds = r.BeatmapSounds, hits = r.BeatmapHits;
            if (ImGui.Checkbox(L.T("simulation.beatmapSounds"), ref sounds)) { r.BeatmapSounds = sounds; simulationDraftStale = true; }
            if (ImGui.Checkbox(L.T("simulation.beatmapHits"), ref hits)) { r.BeatmapHits = hits; simulationDraftStale = true; }
            if (simulationDraftMap == null || simulationDraftMap.Objects.Count == 0) Theme.TextWrapped(L.T("simulation.noBeatmap"));
            else if (simulationDraftMap.Mode > 0) Theme.TextWrapped(L.T("simulation.approximate", simulationDraftMap.Mode switch { 1 => "taiko", 2 => "catch", _ => "mania" }));
        }
        if (r.Problem() is { } problem) { ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError); Theme.TextWrapped(SimulationProblem(problem)); ImGui.PopStyleColor(); }
        EnsureSimulationDraft();
        int index = simulationDraft.IndexOf(r);
        var eventIndices = Enumerable.Range(0, simulationDraftPreview.Stream.Events.Count).Where(i => simulationDraftPreview.Stream.Events[i].RuleIndex == index).ToHashSet();
        var reports = simulationDraftReports.Where(t => t.Fires.Any(f => eventIndices.Contains(f.EventIndex))).ToArray();
        string result = L.T("simulation.ruleResult", eventIndices.Count, reports.Length);
        if (ImGui.TreeNode(result))
        {
            foreach (var report in reports) if (ImGui.Selectable(report.Sprite.Path + " · " + report.Trigger.Name)) LocateSimulationTrigger(report);
            ImGui.TreePop();
        }
    }

    private void DrawSimulationStateTrack()
    {
        EnsureSimulationDraft();
        var bounds = app.TimeBounds;
        double start = bounds.Start, end = Math.Max(bounds.End, start + 1);
        var states = simulationDraftPreview.Stream.Events.Where(e => e.Kind == SimEventKind.StateChange).ToArray();
        if (states.Length > 0) { start = Math.Min(start, states[0].Time); end = Math.Max(end, states[^1].Time + 1); }
        var pos = ImGui.GetCursorScreenPos(); var size = new Vector2(ImGui.GetContentRegionAvail().X, Theme.Px(30));
        var dl = ImGui.GetWindowDrawList();
        float X(double time) => pos.X + (float)((time - start) / (end - start)) * size.X;
        uint Color(bool failing) => failing ? Theme.Rgba(200, 68, 78) : Theme.Rgba(58, 166, 115);
        double previous = start; bool fail = simulationDraftPreview.FailStateAt(start);
        foreach (var e in states.Where(e => e.Time >= start && e.Effective))
        { dl.AddRectFilled(new(X(previous), pos.Y + 8), new(X(e.Time), pos.Y + size.Y), Color(fail)); previous = e.Time; fail = e.Failing; }
        dl.AddRectFilled(new(X(previous), pos.Y + 8), new(X(end), pos.Y + size.Y), Color(fail));
        foreach (var e in states)
        {
            var a = new Vector2(X(e.Time), pos.Y + 9); var b = a + new Vector2(-4, -7); var c = a + new Vector2(4, -7);
            if (e.Effective) dl.AddTriangleFilled(a, b, c, Color(e.Failing)); else dl.AddTriangle(a, b, c, Theme.Rgba(160, 160, 160));
        }
        dl.AddLine(new(X(app.Time), pos.Y), new(X(app.Time), pos.Y + size.Y), Theme.Playhead, 2);
        ImGui.InvisibleButton("##simulationStateTrack", size);
        if (ImGui.IsItemHovered())
        {
            double time = start + (ImGui.GetMousePos().X - pos.X) / size.X * (end - start);
            Theme.Tooltip(Theme.FormatTime(time) + " · " + (simulationDraftPreview.FailStateAt(time) ? "Failing" : "Passing"));
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) app.Seek(time);
        }
    }
}
