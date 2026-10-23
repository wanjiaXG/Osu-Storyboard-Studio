using System.Numerics;
using ImGuiNET;
using SbStudio.Core;

namespace SbStudio.App.Ui;

/// <summary>
/// Edits a trigger's condition as osu!lazer reads it. The sound fields follow the simulation's HitSound
/// rule in name and order (normal bank, addition, addition bank, index), each of them also "any".
/// </summary>
public static class TriggerConditionPicker
{
    private static readonly TriggerKind[] Kinds = { TriggerKind.HitSound, TriggerKind.HitObjectHit, TriggerKind.Passing, TriggerKind.Failing };
    private static readonly string[] Presets =
    {
        "HitSound", "HitSoundClap", "HitSoundFinish", "HitSoundWhistle", "HitSoundNormal", "HitSoundSoft", "HitSoundDrum",
    };
    private static readonly Localized<string[]> Banks = new(() => new[] { L.T("props.triggerAny"), "Normal", "Soft", "Drum" });
    // In the order of the simulation's addition checkboxes; the values are those of TriggerAddition.
    private static readonly TriggerAddition[] AdditionOrder = { TriggerAddition.Any, TriggerAddition.Whistle, TriggerAddition.Finish, TriggerAddition.Clap };
    private static readonly Localized<string[]> Additions = new(() => new[] { L.T("props.triggerAny"), "Whistle", "Finish", "Clap" });

    // The index being typed; checked when the field is left or Enter is pressed.
    private static uint indexId;
    private static string indexText = "";

    /// <summary>Why an index cannot be part of a lazer condition; null when it can (empty is "any").</summary>
    public static string? IndexProblem(string text)
    {
        if (text.Length == 0) return null;
        if (!TriggerCondition.IsValidSuffix(text)) return L.T("props.triggerIndexDigits");
        if (text.Length > 1 && text[0] == '0')
            return L.T("props.triggerIndexLeadingZero", text, text.TrimStart('0').Length == 0 ? "0" : text.TrimStart('0'));
        if (text is "0" or "1") return L.T("props.triggerIndexNoSuffix");
        return text.Length > 9 ? L.T("props.triggerIndexDigits") : null;
    }

    public static bool Draw(EditorApp app, ref string name)
    {
        bool changed = false;
        bool parsed = TriggerCondition.TryParse(name, out var condition);
        float labelWidth = Theme.LabelWidth(78, L.T("props.triggerKind"), L.T("props.triggerNormalBank"), L.T("props.triggerAddition"),
            L.T("props.triggerAdditionBank"), L.T("props.triggerSampleIndex"), L.T("props.triggerName"));

        ImGui.SetNextItemWidth(-labelWidth);
        string preview = parsed ? condition!.Kind.ToString() : name;
        if (ImGui.BeginCombo(L.T("props.triggerKind"), preview, ImGuiComboFlags.HeightLarge))
        {
            foreach (var kind in Kinds)
                if (ImGui.Selectable(kind.ToString(), parsed && condition!.Kind == kind) && !(parsed && condition!.Kind == kind))
                {
                    name = new TriggerCondition(kind).ToName();
                    changed = true;
                }
            ImGui.SeparatorText(L.T("props.triggerPresets"));
            foreach (string preset in Presets)
                if (ImGui.Selectable(preset + "##preset", preset == name) && preset != name)
                {
                    name = preset;
                    changed = true;
                }
            // Imported or unknown conditions keep their text until something else is chosen.
            if (!parsed)
            {
                ImGui.Separator();
                ImGui.Selectable(name + "##unknown", true);
            }
            ImGui.EndCombo();
        }
        Theme.Tooltip(L.T("props.triggerCondition.tip"));

        if (!parsed)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError);
            Theme.TextWrapped(L.T("props.triggerUnknown"));
            ImGui.PopStyleColor();
            return changed;
        }
        var c = changed ? (TriggerCondition.TryParse(name, out var picked) ? picked : condition!) : condition!;

        if (c.Kind == TriggerKind.HitSound)
        {
            int normal = (int)c.NormalBank, additionBank = (int)c.AdditionBank;
            int addition = Array.IndexOf(AdditionOrder, c.Addition);
            bool fields = Combo(L.T("props.triggerNormalBank"), labelWidth, ref normal, Banks.Value);
            fields |= Combo(L.T("props.triggerAddition"), labelWidth, ref addition, Additions.Value);
            Theme.Tooltip(L.T("props.triggerOneAddition"));
            fields |= Combo(L.T("props.triggerAdditionBank"), labelWidth, ref additionBank, Banks.Value);
            string suffix = c.Suffix;
            if (IndexField(app, labelWidth, ref suffix)) fields = true;
            if (fields)
            {
                c = c with
                {
                    NormalBank = (TriggerSampleBank)normal,
                    AdditionBank = (TriggerSampleBank)additionBank,
                    Addition = AdditionOrder[Math.Max(0, addition)],
                    Suffix = suffix,
                };
                name = c.ToName();
                changed = true;
            }
        }

        // The condition as it is written in the script.
        ImGui.SetNextItemWidth(-labelWidth);
        string written = name;
        ImGui.BeginDisabled();
        ImGui.InputText(L.T("props.triggerName"), ref written, 256, ImGuiInputTextFlags.ReadOnly);
        ImGui.EndDisabled();
        Theme.TextDimmed(L.T("props.triggerMeaning", ViewerPanel.SimulationExplain(name)));
        return changed;
    }

    private static bool Combo(string label, float labelWidth, ref int index, string[] items)
    {
        ImGui.SetNextItemWidth(-labelWidth);
        return ImGui.Combo(label, ref index, items, items.Length);
    }

    /// <summary>The index of the samples, typed freely: what lazer would never match is refused with the reason.</summary>
    private static bool IndexField(EditorApp app, float labelWidth, ref string suffix)
    {
        uint id = ImGui.GetID("##triggerIndex");
        ImGui.SetNextItemWidth(-labelWidth);
        string text = indexId == id ? indexText : suffix;
        bool enter = ImGui.InputTextWithHint(L.T("props.triggerSampleIndex") + "##triggerIndex", L.T("props.triggerAny"), ref text, 32,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
        if (ImGui.IsItemActive()) { indexId = id; indexText = text; }
        Theme.Tooltip(L.T("props.triggerSampleIndex.tip"));
        bool done = enter || ImGui.IsItemDeactivated();
        if (!done || indexId != id) return false;
        indexId = 0;
        string typed = text.Trim();
        if (typed == suffix) return false;
        if (IndexProblem(typed) is { } problem)
        {
            app.ShowMessage(L.T("input.invalid.title"), problem);
            return false;
        }
        suffix = typed;
        return true;
    }
}
