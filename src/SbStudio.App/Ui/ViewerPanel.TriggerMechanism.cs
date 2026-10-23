using System.Numerics;
using ImGuiNET;
using SbStudio.Core;

namespace SbStudio.App.Ui;

public sealed unsafe partial class ViewerPanel
{
    private void DrawSimulationMechanism()
    {
        if (ImGui.BeginChild("##simulationMechanism", new(0, 0)))
        {
            MechanismParagraph(L.T("simulation.mech.1"), L.T("simulation.mech.1.body"));
            MechanismParagraph(L.T("simulation.mech.2"), L.T("simulation.mech.2.body"));
            MechanismParagraph(L.T("simulation.mech.3"), L.T("simulation.mech.3.body"));
            MechanismParagraph(L.T("simulation.mech.4"), L.T("simulation.mech.4.body"));
            MechanismParagraph(L.T("simulation.mech.5"), L.T("simulation.mech.5.body"));
            MechanismParagraph(L.T("simulation.mech.6"), L.T("simulation.mech.6.body"));
            MechanismParagraph(L.T("simulation.mech.7"), L.T("simulation.mech.7.body"));
            MechanismParagraph(L.T("simulation.mech.8"), L.T("simulation.mech.8.body"));
            MechanismParagraph(L.T("simulation.mech.9"), L.T("simulation.mech.9.body"));
            ImGui.Separator(); ImGui.TextUnformatted(L.T("simulation.mech.tables"));
            if (ImGui.BeginTable("##simulationValues", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn(L.T("simulation.mech.variable")); ImGui.TableSetupColumn(L.T("simulation.kind")); ImGui.TableSetupColumn(L.T("simulation.mech.parameters")); ImGui.TableHeadersRow();
                MechanismRow("Drawable.Time.Current", L.T("simulation.kindFilter.all"), L.T("simulation.offset") + " · " + L.T("simulation.repeat") + " · " + L.T("simulation.interval"));
                MechanismRow("GameplayState.LastPlayedSamples", "HitSound", L.T("simulation.sampleKind") + " · " + L.T("simulation.normalBank") + " · " + L.T("simulation.additions") + " · " + L.T("simulation.additionBank") + " · " + L.T("simulation.index") + " · " + L.T("simulation.fileSample"));
                MechanismRow("GameplayState.LastJudgementResult", "HitObjectHit", L.T("simulation.explain.hit"));
                MechanismRow("DrawableStoryboard.passing", "Pass/Fail", L.T("simulation.state.tip"));
                MechanismRow(L.T("simulation.kind.beatmap"), "HitSound / HitObjectHit", L.T("simulation.from") + " · " + L.T("simulation.to") + " · " + L.T("simulation.beatmapSounds") + " · " + L.T("simulation.beatmapHits"));
                ImGui.EndTable();
            }
            if (ImGui.BeginTable("##simulationParameters", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn(L.T("simulation.kind")); ImGui.TableSetupColumn(L.T("simulation.mech.parameters")); ImGui.TableSetupColumn(L.T("simulation.col.note")); ImGui.TableHeadersRow();
                MechanismRow(L.T("simulation.kindFilter.all"), L.T("simulation.name") + " · " + L.T("simulation.ruleEnabled"), L.T("simulation.confirm.tip"));
                MechanismRow("HitSound / HitObjectHit / Pass/Fail", L.T("simulation.offset"), L.T("simulation.offset.tip"));
                MechanismRow("HitSound / HitObjectHit", L.T("simulation.repeat") + " · " + L.T("simulation.interval"), L.T("simulation.repeat.invalid") + " " + L.T("simulation.interval.invalid"));
                MechanismRow("HitSound", L.T("simulation.sampleKind"), L.T("simulation.sampleKind.tip"));
                MechanismRow("HitSound", L.T("simulation.normalBank") + " · " + L.T("simulation.additionBank"), "Normal / Soft / Drum");
                MechanismRow("HitSound", L.T("simulation.additions"), "Whistle / Finish / Clap");
                MechanismRow("HitSound", L.T("simulation.index"), L.T("simulation.index.tip"));
                MechanismRow("HitSound", L.T("simulation.fileSample"), L.T("simulation.fileSample.tip"));
                MechanismRow("Pass/Fail", L.T("simulation.state"), "Passing / Failing");
                MechanismRow(L.T("simulation.kind.beatmap"), L.T("simulation.from") + " · " + L.T("simulation.to"), L.T("simulation.range.any") + " · " + L.T("simulation.range.invalid"));
                MechanismRow(L.T("simulation.kind.beatmap"), L.T("simulation.beatmapSounds") + " · " + L.T("simulation.beatmapHits"), L.T("simulation.note.approximate.detail"));
                ImGui.EndTable();
            }
            if (ImGui.BeginTable("##simulationExamples", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn(L.T("simulation.mech.example")); ImGui.TableSetupColumn(L.T("simulation.mech.parameters")); ImGui.TableHeadersRow();
                MechanismExample(L.T("simulation.example.1"), "HitSound: 12000 ms · Soft · Clap (Drum) · 1; HitObjectHit: 12000 ms");
                MechanismExample(L.T("simulation.example.2"), "HitSound: Clap · 20000 ms · ×16 · 500 ms");
                MechanismExample(L.T("simulation.example.3"), "HitSound: Whistle; slidertick ×2; Clap; HitObjectHit ×4");
                MechanismExample(L.T("simulation.example.4"), "Pass/Fail: 30000 ms Failing; 45000 ms Passing");
                MechanismExample(L.T("simulation.example.5"), L.T("simulation.kind.beatmap") + ": 60000–90000 ms");
                MechanismExample(L.T("simulation.example.6"), "Pass/Fail: Failing → Passing");
                ImGui.EndTable();
            }
        }
        ImGui.EndChild();
    }
    private static void MechanismParagraph(string title, string body)
    { ImGui.Separator(); ImGui.TextUnformatted(title); Theme.TextWrapped(body); ImGui.Spacing(); }
    private static void MechanismRow(string variable, string kind, string parameters)
    { ImGui.TableNextRow(); ImGui.TableSetColumnIndex(0); Theme.TextWrapped(variable); ImGui.TableSetColumnIndex(1); Theme.TextWrapped(kind); ImGui.TableSetColumnIndex(2); Theme.TextWrapped(parameters); }
    private static void MechanismExample(string example, string parameters)
    { ImGui.TableNextRow(); ImGui.TableSetColumnIndex(0); Theme.TextWrapped(example); ImGui.TableSetColumnIndex(1); Theme.TextWrapped(parameters); }
}
