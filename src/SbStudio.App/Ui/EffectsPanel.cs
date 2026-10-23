using System.Numerics;
using ImGuiNET;
using SbStudio.App.Effects;
using SbStudio.Core;

namespace SbStudio.App.Ui;

internal sealed class EffectsPanel(EditorApp app)
{
    private string search = "", error = "", project = "", category = "";
    private int filter;
    private bool favorites;
    private Task? operation;
    private Task<IReadOnlyList<EffectCatalogEntry>>? loading;
    private EffectCatalogEntry? trustEntry;
    private string? trustFolder;
    private bool openTrust;
    internal bool Busy => operation != null || loading != null;
    internal void Draw()
    {
        if (project != app.Project?.Folder) { project = app.Project?.Folder ?? ""; app.RefreshEffectLibrary(); }
        if (loading is { IsCompleted: true })
        {
            try { app.AddDevelopmentEntries(loading.GetAwaiter().GetResult()); } catch (Exception ex) { error = ex.Message; }
            loading = null;
        }
        if (operation is { IsCompleted: true })
        { try { operation.GetAwaiter().GetResult(); } catch (Exception ex) { error = ex.Message; } operation = null; }
        ImGui.BeginDisabled(operation != null || loading != null);
        if (UiScriptTargets.Item("library.manage", ImGui.Button(L.T("effects.manage")))) ImGui.OpenPopup("##libraryActions");
        ImGui.SameLine(); if (UiScriptTargets.Item("library.refresh", ImGui.Button(L.T("effects.refresh")))) Run(app.RefreshEffectLibrary);
        if (ImGui.BeginPopup("##libraryActions"))
        {
            if (UiScriptTargets.Item("library.install", ImGui.MenuItem(L.T("effects.install")))) Run(() => { var file = EffectFileDialogs.Open("sbeffect"); if (file.IsOk) app.InstallEffectPackage(file.Path); });
            if (UiScriptTargets.Item("library.new", ImGui.MenuItem(L.T("effects.new")))) Run(() => { string folder = app.CreateDevelopmentEffect(); loading = app.LoadDevelopmentEffectAsync(folder, true); if (!AppSettings.ReadOnly) AssetsPanel.OpenInExplorer(Path.Combine(folder, "Example.cs"), true); });
            if (UiScriptTargets.Item("library.load", ImGui.MenuItem(L.T("effects.loadFolder"))))
            { var folder = EffectFileDialogs.Folder(); if (folder.IsOk) { trustFolder = folder.Path; trustEntry = null; openTrust = true; } }
            if (UiScriptTargets.Item("library.category", ImGui.BeginMenu(L.T("effects.category"))))
            {
                if (ImGui.MenuItem(L.T("effects.all"), null, category.Length == 0)) category = "";
                foreach (string item in app.EffectLibrary.Select(e => e.Definition.Category).Distinct().Order())
                    if (ImGui.MenuItem(item, null, category == item)) category = item;
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu(L.T("effects.execution")))
            {
                ImGui.BeginDisabled(app.ImportingPlugins != 0);
                int workers = app.Settings.EffectWorkerCount, seconds = app.Settings.EffectTimeoutSeconds;
                bool changed = ImGui.InputInt(L.T("effects.workers"), ref workers);
                changed |= ImGui.InputInt(L.T("effects.timeout"), ref seconds);
                if (changed) app.ConfigureEffectExecution(workers, seconds);
                ImGui.EndDisabled(); ImGui.EndMenu();
            }
            ImGui.EndPopup();
        }
        ImGui.EndDisabled();
        ImGui.SetNextItemWidth(-1); UiScriptTargets.Item("library.search", ImGui.InputTextWithHint("##effectSearch", L.T("effects.search"), ref search, 256));
        ImGui.SetNextItemWidth(Math.Max(80, ImGui.GetContentRegionAvail().X * .55f));
        string[] sources = [L.T("effects.all"), L.T("effects.builtin"), L.T("effects.installed"), L.T("effects.project"), L.T("effects.development")];
        if (UiScriptTargets.Item("library.filter", ImGui.BeginCombo("##effectSource", sources[filter])))
        {
            for (int i = 0; i < sources.Length; i++)
                if (UiScriptTargets.Item("library.filter." + i, ImGui.Selectable(sources[i], filter == i))) filter = i;
            ImGui.EndCombo();
        }
        Theme.Tooltip(L.T("effects.source")); ImGui.SameLine(); UiScriptTargets.Item("library.favorites", ImGui.Checkbox(L.T("effects.favorites"), ref favorites));
        if (loading != null || operation != null) Theme.TextDimmed(L.T("effects.working"));
        if (error.Length > 0) { Theme.TextWrapped(error); if (ImGui.SmallButton(L.T("effects.dismiss"))) error = ""; }
        if (app.EffectsCatalog.Errors.Count > 0 && ImGui.TreeNode(L.T("effects.details")))
        { foreach (string issue in app.EffectsCatalog.Errors) Theme.TextWrapped(issue); ImGui.TreePop(); }
        ImGui.Separator();
        foreach (var entry in app.EffectLibrary.ToArray())
        {
            if (filter > 0 && entry.SourceKind != new[] { "", "builtin", "installed", "project", "development" }[filter]) continue;
            if (favorites && !app.IsEffectFavorite(entry.Definition.EffectId)) continue;
            if (category.Length > 0 && entry.Definition.Category != category) continue;
            if (search.Length > 0 && !(EffectDisplay.Name(entry.Definition) + " " + entry.Definition.Name + " " + entry.Definition.Category + " " + entry.Definition.EffectId).Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            ImGui.PushID(entry.Directory + entry.Definition.EffectId);
            float addWidth = ImGui.CalcTextSize(L.T("effects.add")).X + Theme.Px(20);
            if (ImGui.Selectable(EffectDisplay.Name(entry.Definition) + "##effect", false, ImGuiSelectableFlags.AllowDoubleClick, new Vector2(Math.Max(20, ImGui.GetContentRegionAvail().X - addWidth - Theme.Px(8)), Theme.Px(24))) && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) Add(entry);
            UiScriptTargets.Item("library.entry." + entry.Definition.EffectId, false);
            Theme.Tooltip(entry.Definition.Description + "\n" + entry.Definition.Category + " · " + entry.Manifest.PackageVersion);
            if (ImGui.BeginDragDropSource())
            {
                unsafe { int marker = 1; ImGui.SetDragDropPayload("SB_EFFECT", (nint)(&marker), sizeof(int)); }
                ImGui.TextUnformatted(entry.Definition.Name); ImGui.EndDragDropSource(); app.DraggedEffect = entry;
            }
            if (ImGui.BeginPopupContextItem())
            {
                if (ImGui.MenuItem(L.T("effects.add"))) Add(entry);
                if (UiScriptTargets.Item("library.favorite", ImGui.MenuItem(L.T("effects.favorite"), null, app.IsEffectFavorite(entry.Definition.EffectId)))) app.ToggleEffectFavorite(entry.Definition.EffectId);
                if (ImGui.MenuItem(L.T("plugin.openFolder"))) AssetsPanel.OpenInExplorer(entry.Directory, false);
                if (UiScriptTargets.Item("library.pack", ImGui.MenuItem(L.T("effects.pack"))))
                {
                    var file = EffectFileDialogs.Save("sbeffect");
                    if (file.IsOk) operation = app.PackEffectAsync(entry, file.Path.EndsWith(".sbeffect", StringComparison.OrdinalIgnoreCase) ? file.Path : file.Path + ".sbeffect");
                }
                if (entry.SourceKind == "installed" && UiScriptTargets.Item("library.remove", ImGui.MenuItem(L.T("effects.remove")))) Run(() => app.RemoveInstalledEffect(entry));
                ImGui.EndPopup();
            }
            ImGui.SameLine(); if (UiScriptTargets.Item("library.add." + entry.Definition.EffectId, ImGui.SmallButton(L.T("effects.add")))) Add(entry);
            ImGui.PopID();
        }
        if (openTrust) { ImGui.OpenPopup("##effectTrust"); openTrust = false; }
        bool show = true;
        if (ImGui.IsPopupOpen("##effectTrust"))
            ImGui.SetNextWindowSizeConstraints(new Vector2(Theme.Px(420), 0), new Vector2(Theme.Px(560), float.MaxValue));
        if (ImGui.BeginPopupModal("##effectTrust", ref show, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped(L.T("effects.trustPrompt")); ImGui.TextWrapped(trustFolder ?? trustEntry?.Definition.Name ?? "");
            if (UiScriptTargets.Item("library.trust", ImGui.Button(L.T("effects.trust"))))
            {
                if (trustFolder != null) loading = app.LoadDevelopmentEffectAsync(trustFolder, true);
                else if (trustEntry != null) Run(() => { app.TrustEffect(trustEntry); app.AddEffect(trustEntry with { Trusted = true }); });
                trustFolder = null; trustEntry = null; ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine(); if (ImGui.Button(L.T("common.cancel"))) ImGui.CloseCurrentPopup(); ImGui.EndPopup();
        }
    }
    private void Add(EffectCatalogEntry entry)
    {
        if (!entry.Trusted) { trustEntry = entry; trustFolder = null; openTrust = true; return; }
        Run(() => app.AddEffect(entry));
    }
    private void Run(Action action) { try { action(); } catch (Exception ex) { error = ex.Message; } }
}
