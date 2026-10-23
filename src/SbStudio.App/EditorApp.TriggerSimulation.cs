using System.Text.Json;
using SbStudio.Core;

namespace SbStudio.App;

public sealed partial class EditorApp
{
    private sealed record SimulationHistory(TriggerSimulationRule[] Rules);
    private static SimulationHistory CaptureSimulation(ProjectSettings settings) => new(settings.TriggerSimulationRules.Select(r => r.Clone()).ToArray());
    public readonly TriggerSimulation TriggerSimulation = new();
    private OsuBeatmap? simulationMap;
    private int simulationReportsRevision = -1;
    private IReadOnlyList<TriggerReport> simulationReports = Array.Empty<TriggerReport>();
    private SbSprite[] simulationPreparing = Array.Empty<SbSprite>();
    private int simulationPrepared;
    public bool SimulationPreparing => simulationPrepared < simulationPreparing.Length;
    public event Action<SbSprite, SbTrigger>? SimulationRequested;
    public void ShowSimulationTrigger(SbSprite sprite, SbTrigger trigger) => SimulationRequested?.Invoke(sprite, trigger);
    /// <summary>Asks the simulation for a draft rule that fires the trigger; it still takes Confirm to apply.</summary>
    public event Action<SbSprite, SbTrigger, TriggerCondition>? SimulationRuleRequested;
    public void RequestSimulationRule(SbSprite sprite, SbTrigger trigger, TriggerCondition condition) =>
        SimulationRuleRequested?.Invoke(sprite, trigger, condition);

    public SpriteState PreviewState(SbSprite sprite, double time) =>
        RenderOptions.Simulation?.Evaluate(sprite, time) ?? sprite.ShownState(time);

    public IReadOnlyList<TriggerReport> SimulationReports
    {
        get
        {
            EnsureTriggerSimulation();
            if (simulationReportsRevision != DocRevision)
            {
                simulationReports = TriggerSimulation.Reports(Doc);
                simulationReportsRevision = DocRevision;
            }
            return simulationReports;
        }
    }

    private void EnsureTriggerSimulation()
    {
        if (simulationMap != Beatmap) RefreshTriggerSimulation();
    }

    public void RefreshTriggerSimulation()
    {
        simulationMap = Beatmap;
        var rules = Project?.Settings.TriggerSimulationRules ?? new();
        TriggerSimulation.Configure(TriggerEventStream.Build(rules, simulationMap));
        RenderOptions.Simulation = rules.Any(r => r.Enabled) ? TriggerSimulation : null;
        simulationReportsRevision = -1;
        simulationReports = Project == null ? Array.Empty<TriggerReport>() : TriggerSimulation.Reports(Doc);
        simulationReportsRevision = DocRevision;
        // Prepare large replays behind the same loading screen used for projects. No partial frame is shown.
        simulationPreparing = rules.Any(r => r.Enabled) && TriggerSimulation.Stream.Events.Count * (long)simulationReports.Count > 30000
            ? Doc.Sprites.Where(s => s.Timeline.HasTriggers).ToArray() : Array.Empty<SbSprite>();
        simulationPrepared = 0;
    }

    private void PrepareSimulationFrame()
    {
        EnsureTriggerSimulation();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (SimulationPreparing && timer.ElapsedMilliseconds < 12)
            TriggerSimulation.Evaluate(simulationPreparing[simulationPrepared++], Time);
    }

    public static bool SameSimulationRules(IEnumerable<TriggerSimulationRule> a, IEnumerable<TriggerSimulationRule> b) =>
        JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);

    public bool ApplySimulationRules(IReadOnlyList<TriggerSimulationRule> rules)
    {
        if (Project == null || rules.Any(r => r.Problem() != null) || SameSimulationRules(Project.Settings.TriggerSimulationRules, rules)) return false;
        CommitEdits();
        Project.Settings.TriggerSimulationRules = rules.Select(r => r.Clone()).ToList();
        Project.Settings.TriggerSimulationEnabled = true;
        Touch();
        RefreshTriggerSimulation();
        RefreshPlayback();
        CommitEdits();
        if (committed != null) committed = committed with { HistoryLabel = new(HistoryAction.ApplySimulationRules) };
        return true;
    }
}
