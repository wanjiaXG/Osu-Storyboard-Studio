using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using SbStudio.Core;
using SbStudio.Effects;

namespace SbStudio.App.Ui;

public sealed partial class PropertiesPanel
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<EditorApp, Dictionary<string, JsonElement>> effectDrafts = new();
    private sealed class EffectUiState { internal Task<EditorApp.EffectUpgrade>? Upgrade; internal string Error=""; internal SbObjectGroup? Trust; internal Effects.EffectCatalogEntry? UpgradeEntry; internal long PreviewTick; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<EditorApp, EffectUiState> effectUi = new();
    internal static bool EffectsBusy(EditorApp app) => effectUi.TryGetValue(app, out var state) && state.Upgrade != null;
    internal static void UpdateEffects(EditorApp app)
    {
        if (!effectUi.TryGetValue(app, out var state) || state.Upgrade is not { IsCompleted: true }) return;
        try { app.ApplyEffectUpgrade(state.Upgrade.GetAwaiter().GetResult()); }
        catch (Exception ex) { state.Error = ex.Message; }
        state.Upgrade = null;
    }
    internal static void DrawEffectParameters(EditorApp app, SbObjectGroup group)
    {
        var binding = group.Plugin?.Effect;
        if (binding?.Supported != true || binding.Definition is not {} definition) { Theme.TextWrapped(L.T("effects.incompatible")); return; }
        Dictionary<string, JsonElement> parameters;
        try { parameters = EffectBinding.Parameters(group.Plugin!); }
        catch (JsonException ex) { Theme.TextWrapped(ex.Message); return; }
        foreach (var p in definition.Parameters) parameters.TryAdd(p.Id, p.Default);
        var drafts = effectDrafts.GetOrCreateValue(app); string section = "";
        var state=effectUi.GetOrCreateValue(app);
        if (app.Plugins.TryGetValue(group.Plugin!.Instance, out var running) && running.Status == PluginStatus.Importing)
            ImGui.ProgressBar((float)running.Progress, new Vector2(-1, 0), running.Stage switch
            {
                "compile" => L.T("effects.progress.compile"), "describe" => L.T("effects.progress.describe"),
                "generate" => L.T("effects.progress.generate"), "output" => L.T("effects.progress.output"),
                _ => L.T("effects.progress.waiting")
            });
        try
        {
            if(UiScriptTargets.Item("effect.savePreset", ImGui.Button(L.T("effects.savePreset"))))
            {var file=EffectFileDialogs.Save("sbpreset");if(file.IsOk) app.SaveEffectPreset(group,file.Path.EndsWith(".sbpreset",StringComparison.OrdinalIgnoreCase)?file.Path:file.Path+".sbpreset");}
            ImGui.SameLine();
            if(UiScriptTargets.Item("effect.loadPreset", ImGui.Button(L.T("effects.loadPreset"))))
            {var file=EffectFileDialogs.Open("sbpreset");if(file.IsOk) app.ApplyEffectPreset(group,file.Path);}
            if (UiScriptTargets.Item("effect.manage", ImGui.CollapsingHeader(L.T("effects.manage"))))
            {
            if (UiScriptTargets.Item("effect.presets", ImGui.BeginCombo(L.T("effects.presets"), L.T("effects.choose"))))
            {
                foreach (string preset in Effects.EffectPresets.Bundled(app.Project!, group.Plugin!))
                {
                    string name = Path.GetFileNameWithoutExtension(preset) switch
                    {
                        "soft-snow" => L.T("effects.preset.snow"), "slow-push" => L.T("effects.preset.push"),
                        "soft-pulse" => L.T("effects.preset.pulse"), var other => other
                    };
                    if (UiScriptTargets.Item("effect.preset." + Path.GetFileNameWithoutExtension(preset), ImGui.Selectable(name))) app.ApplyEffectPreset(group, preset);
                }
                ImGui.EndCombo();
            }
            if(binding.SourceKind=="development" && UiScriptTargets.Item("effect.copySource", ImGui.Button(L.T("effects.copySource")))) app.CopyEffectSourceIntoProject(group);
            ImGui.BeginDisabled(state.Upgrade!=null);
            if(UiScriptTargets.Item("effect.upgrade", ImGui.BeginCombo(L.T("effects.upgrade"),binding.PackageVersion)))
            {
                foreach(var candidate in app.EffectLibrary.Where(e=>e.SourceKind!="development" && e.Definition.EffectId==binding.EffectId && e.Manifest.ContentHash!=binding.PackageHash))
                    if(UiScriptTargets.Item("effect.version." + candidate.Manifest.PackageVersion, ImGui.Selectable(candidate.Manifest.PackageVersion+"##"+candidate.Manifest.ContentHash)))
                    {
                        if (candidate.Trusted) state.Upgrade=app.PrepareEffectUpgradeAsync(group,candidate);
                        else { state.Trust=group; state.UpgradeEntry=candidate; }
                    }
                ImGui.EndCombo();
            }
            ImGui.EndDisabled();
            if(UiScriptTargets.Item("effect.trust", ImGui.Button(L.T("effects.trust")))) {state.Trust=group;state.UpgradeEntry=null;ImGui.OpenPopup("##trustInstance");}
            }
            if (state.UpgradeEntry != null && !ImGui.IsPopupOpen("##trustInstance")) ImGui.OpenPopup("##trustInstance");
            bool open=true;
            if (ImGui.IsPopupOpen("##trustInstance"))
                ImGui.SetNextWindowSizeConstraints(new Vector2(Theme.Px(420), 0), new Vector2(Theme.Px(560), float.MaxValue));
            if(ImGui.BeginPopupModal("##trustInstance",ref open,ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.TextWrapped(L.T("effects.trustPrompt"));
                if (state.UpgradeEntry is {} requested) ImGui.TextWrapped(requested.Definition.Name + " · " + requested.Manifest.PackageVersion);
                if(UiScriptTargets.Item("effect.trust", ImGui.Button(L.T("effects.trust"))))
                {
                    if (state.UpgradeEntry is {} entry && state.Trust is {} target)
                    { app.TrustEffect(entry); state.Upgrade=app.PrepareEffectUpgradeAsync(target,entry with {Trusted=true}); }
                    else if(state.Trust!=null) app.TrustEffect(state.Trust);
                    state.UpgradeEntry=null; ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();if(ImGui.Button(L.T("common.cancel"))) {state.UpgradeEntry=null;ImGui.CloseCurrentPopup();}ImGui.EndPopup();
            }
            if (!open) state.UpgradeEntry=null;
        }
        catch(Exception ex) {state.Error=ex.Message;}
        if(state.Error.Length>0) {Theme.TextWrapped(state.Error);if(ImGui.SmallButton(L.T("effects.dismiss"))) state.Error="";}
        ImGui.SeparatorText(L.T("effects.parameters"));
        foreach (var p in definition.Parameters)
        {
            if (p.VisibleWhen?.Evaluate(parameters) == false) continue;
            if (p.Section.Length > 0 && p.Section != section) { ImGui.SeparatorText(p.Section); section = p.Section; }
            ImGui.PushID(p.Id); string key = binding.InstanceGuid + ":" + p.Id;
            JsonElement value = drafts.GetValueOrDefault(key, parameters[p.Id]); bool changed = false, immediate = false;
            try
            {
                ImGui.TextUnformatted(Effects.EffectDisplay.Parameter(definition, p) + (p.Unit.Length > 0 ? " (" + p.Unit + ")" : ""));
                if (p.Description.Length > 0) Theme.Tooltip(p.Description);
                ImGui.SameLine();
                if (ImGui.SmallButton(L.T("effects.reset"))) { app.FinishEffectEdit(); app.SetEffectParameter(group, p.Id, p.Default); drafts.Remove(key); value = p.Default; }
                ImGui.SetNextItemWidth(-1);
                switch (p.Type)
                {
                    case ParameterType.Toggle:
                        bool enabled = value.GetBoolean(); changed = ImGui.Checkbox("##value", ref enabled); value = JsonSerializer.SerializeToElement(enabled); immediate = true; break;
                    case ParameterType.Integer:
                        int integer = value.GetInt32(); changed = ImGui.InputInt("##value", ref integer); value = JsonSerializer.SerializeToElement(integer); break;
                    case ParameterType.Number: case ParameterType.Time:
                        double number = value.GetDouble(); changed = ImGui.InputDouble("##value", ref number, p.Step ?? 0, 0, "%.4f"); value = JsonSerializer.SerializeToElement(number); break;
                    case ParameterType.Choice:
                        var choices = p.Choices?.ToArray() ?? []; int selected = Array.IndexOf(choices, value.GetString());
                        changed = ImGui.Combo("##value", ref selected, choices, choices.Length);
                        if (changed) value = JsonSerializer.SerializeToElement(choices[selected]); immediate = true; break;
                    case ParameterType.Text: case ParameterType.Multiline:
                        string text = value.GetString() ?? "";
                        changed = p.Type == ParameterType.Multiline ? ImGui.InputTextMultiline("##value", ref text, 8192, new Vector2(-1, Theme.Px(80))) : ImGui.InputText("##value", ref text, 8192);
                        value = JsonSerializer.SerializeToElement(text); break;
                    case ParameterType.Point2:
                        var point = value.Deserialize<Point2>(EffectJson.Options); var vector = new Vector2((float)point.X, (float)point.Y);
                        changed = ImGui.InputFloat2("##value", ref vector); value = JsonSerializer.SerializeToElement(new Point2(vector.X, vector.Y), EffectJson.Options); break;
                    case ParameterType.Color:
                        var color = value.Deserialize<ColorValue>(EffectJson.Options); var rgb = new Vector3(color.R / 255f, color.G / 255f, color.B / 255f);
                        changed = ImGui.ColorEdit3("##value", ref rgb);
                        value = JsonSerializer.SerializeToElement(new ColorValue((byte)Math.Clamp(Math.Round(rgb.X * 255), 0, 255), (byte)Math.Clamp(Math.Round(rgb.Y * 255), 0, 255), (byte)Math.Clamp(Math.Round(rgb.Z * 255), 0, 255)), EffectJson.Options); break;
                    case ParameterType.Image: case ParameterType.Animation: case ParameterType.File:
                        var asset = value.Deserialize<AssetReference>(EffectJson.Options)!;
                        if (ImGui.SmallButton(L.T("effects.choose")))
                        {
                            var picked = EffectFileDialogs.Open(p.Type == ParameterType.File ? null : "png,jpg,jpeg,bmp,gif");
                            if (picked.IsOk)
                            {
                                var imported = app.ImportEffectAsset(picked.Path, p.Type == ParameterType.Animation);
                                value = JsonSerializer.SerializeToElement(imported, EffectJson.Options); changed = immediate = true; app.RefreshAssets();
                            }
                        }
                        Theme.TextDimmed(asset.Path); break;
                    default: Theme.TextWrapped(L.T("effects.incompatible")); break;
                }
                UiScriptTargets.Item("parameter." + p.Id, false);
                bool finished = ImGui.IsItemDeactivatedAfterEdit();
                if (changed) drafts[key] = value;
                if (changed && !immediate && Environment.TickCount64 - state.PreviewTick >= 150)
                { app.SetEffectParameter(group, p.Id, value, true); state.PreviewTick = Environment.TickCount64; }
                if ((immediate && changed || finished) && drafts.TryGetValue(key, out var pending))
                { app.SetEffectParameter(group, p.Id, pending, !immediate); app.FinishEffectEdit(); drafts.Remove(key); }
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException or IOException) { if (!ImGui.IsAnyItemActive()) app.FinishEffectEdit(); Theme.TextWrapped(ex.Message); }
            ImGui.PopID();
        }
        if (definition.TimeMode == TimeMode.AdjustableDuration)
        {
            double duration = binding.DurationMs ?? definition.DefaultDuration;
            if (ImGui.InputDouble(L.T("effects.duration"), ref duration, 100, 1000, "%.0f", ImGuiInputTextFlags.EnterReturnsTrue)) app.SetEffectDuration(group, duration);
        }
        var difficulties = new[] { L.T("effects.shared") }.Concat(app.Project!.Difficulties.Select(d => d.DisplayName)).ToArray();
        int current = binding.OutputDifficulty == null ? 0 : app.Project.Difficulties.ToList().FindIndex(d => d.File == binding.OutputDifficulty) + 1;
        if (ImGui.Combo(L.T("effects.output"), ref current, difficulties, difficulties.Length)) app.SetEffectOutputDifficulty(group, current == 0 ? null : app.Project.Difficulties[current - 1].File);
        if (ImGui.TreeNode(L.T("effects.details")))
        {
            ImGui.TextWrapped(binding.PackageId + " · " + binding.PackageVersion + "\n" + binding.SourceLocation);
            ImGui.TextWrapped(binding.CodeRevision); ImGui.TextWrapped(L.T("effects.input") + ": " + binding.InputDifficulty);
            foreach (string orphan in parameters.Keys.Where(k => definition.Parameters.All(p => p.Id != k))) ImGui.TextWrapped(L.T("effects.orphan", orphan));
            ImGui.TreePop();
        }
    }
}
