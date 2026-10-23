using System.Numerics;
using ImGuiNET;
using SbStudio.Core;

namespace SbStudio.App.Ui;

public sealed partial class TimelinePanel
{
    private void DrawTriggerFirings(ImDrawListPtr dl, SbSprite sprite, SbTrigger trigger, float y, float height, float right)
    {
        if (app.RenderOptions.Simulation is not { } simulation) return;
        var fires = simulation.FiresOf(sprite, trigger);
        int lo = 0, hi = fires.Count;
        while (lo < hi) { int m = (lo + hi) / 2; if (fires[m].Time < viewStart) lo = m + 1; else hi = m; }
        var mouse = ImGui.GetMousePos();
        for (int i = lo; i < fires.Count && fires[i].Time <= TimeAt(right); i++)
        {
            var fire = fires[i]; float x = X(fire.Time);
            dl.AddLine(new(x, y + 2), new(x, y + height - 2), fire.Alive ? Theme.Accent : Theme.WithAlpha(Theme.Accent, 0.3f), Theme.Px(2));
            if (MathF.Abs(mouse.X - x) < Theme.Px(4) && mouse.Y >= y && mouse.Y <= y + height && ImGui.IsWindowHovered())
            {
                var e = simulation.Stream.Events[fire.EventIndex];
                var rules = app.Project!.Settings.TriggerSimulationRules;
                string origin = e.RuleIndex >= 0 && e.RuleIndex < rules.Count ? rules[e.RuleIndex].Name : "";
                if (origin.Length == 0 && e.RuleIndex >= 0) origin = (e.RuleIndex + 1).ToString();
                ImGui.BeginTooltip(); ImGui.TextUnformatted(L.T("simulation.timeline.fire", fire.Time.ToString("0.###"), origin)); ImGui.EndTooltip();
            }
        }
    }
}
