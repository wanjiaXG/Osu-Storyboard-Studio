using System.Numerics;
using ImGuiNET;
using SbStudio.Core;

namespace SbStudio.App.Ui;

public sealed partial class MainUi
{
    private string pluginFile="",pluginRoot="";
    private Action? pluginExport;
    private bool pluginExportApproved;
    private bool pluginWaitForLatest, conversionOpen, conversionWait;
    private bool CheckPluginExport(Action proceed)
    {
        if(pluginExportApproved) {pluginExportApproved=false;return true;}
        if(app.Doc.Groups.Any(g=>g.Plugin?.Kind==PluginKind.Effect && string.IsNullOrEmpty(g.Plugin.Key))) {app.SetStatus(L.T("effects.noResult"));return false;}
        if(app.ImportingPlugins>0) {pluginExport=proceed;openPopup="###pluginExport";return false;}
        if(AppSettings.ReadOnly || app.StalePlugins==0) return true;
        pluginExport=proceed;openPopup="###pluginExport";return false;
    }
    private void DrawPluginExportDialog(Vector2 centre)
    {
        DrawEffectConversion(centre);
        if(!BeginModal(L.T("menu.plugins")+"###pluginExport",centre,560)) return;
        ImGui.TextWrapped(L.T("effects.stale"));
        if (app.StalePlugins > 0) ImGui.TextWrapped(L.T("export.pluginStale", app.StalePlugins));
        if (pluginWaitForLatest && app.ImportingPlugins == 0)
        {
            pluginWaitForLatest = false;
            if (app.StalePlugins == 0) { var ready = pluginExport; pluginExport = null; pluginExportApproved = true; ImGui.CloseCurrentPopup(); ready?.Invoke(); ImGui.EndPopup(); return; }
        }
        ImGui.BeginDisabled(app.ImportingPlugins == 0);
        if(ImGui.Button(L.T("effects.waitLatest"))) pluginWaitForLatest = true;
        ImGui.EndDisabled();
        if (pluginWaitForLatest) Theme.TextDimmed(L.T("effects.working"));
        if(ImGui.Button(L.T("effects.useLast")))
        {var run=pluginExport;pluginExport=null;pluginWaitForLatest=false;pluginExportApproved=true;ImGui.CloseCurrentPopup();run?.Invoke();}
        ImGui.SameLine();if(ImGui.Button(L.T("common.cancel"))) {pluginExport=null;ImGui.CloseCurrentPopup();}
        ImGui.EndPopup();
    }
    private void DrawEffectConversion(Vector2 centre)
    {
        if (app.PendingEffectConversion is not {} pending) { conversionOpen = false; return; }
        if (!app.Doc.Groups.Contains(pending.Group) || pending.Group.Plugin == null) { app.PendingEffectConversion = null; return; }
        if (!conversionOpen) { ImGui.OpenPopup("###effectConversion"); conversionOpen = true; conversionWait = false; }
        if (!BeginModal(L.T("plugin.detach") + "###effectConversion", centre, 560)) return;
        app.Plugins.TryGetValue(pending.Group.Plugin.Instance, out var state);
        ImGui.TextWrapped(L.T("effects.stale"));
        bool ready = state?.Status == PluginStatus.Ready;
        if (UiScriptTargets.Item("conversion.useLast", ImGui.Button(L.T("effects.useLast"))) || conversionWait && ready)
        {
            app.PendingEffectConversion = null;
            if (pending.Copy) app.DetachPluginCopy(pending.Group, true); else app.DetachPlugin(pending.Group, true);
            ImGui.CloseCurrentPopup();
        }
        ImGui.BeginDisabled(state?.Status != PluginStatus.Importing);
        if (ImGui.Button(L.T("effects.waitLatest"))) conversionWait = true;
        ImGui.EndDisabled();
        if (ImGui.Button(L.T("common.cancel"))) { app.PendingEffectConversion = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }
    /// <summary>Picks the osb file, then asks where its assets are: from the plugins panel.</summary>
    private void RequestLinkPlugin()
    {
        var result=NativeFileDialogSharp.Dialog.FileOpen("osb",null);
        if(result.IsOk) {pluginFile=result.Path;pluginRoot=Path.GetDirectoryName(result.Path)!;openPopup="###linkPlugin";}
    }
    private void DrawLinkPluginDialog(Vector2 centre)
    {
        if(!BeginModal(L.T("menu.plugins.link")+"###linkPlugin",centre,600)) return;
        ImGui.InputText(L.T("plugin.file"),ref pluginFile,4096);
        ImGui.InputText(L.T("plugin.root"),ref pluginRoot,4096);
        Theme.TextDimmed(L.T("plugin.rootDefault"));
        ImGui.SameLine();if(ImGui.SmallButton(L.T("plugin.change")))
        {var result=NativeFileDialogSharp.Dialog.FolderPicker(pluginRoot);if(result.IsOk) pluginRoot=result.Path;}
        if(ImGui.Button(L.T("common.ok"))) {app.LinkOsb(pluginFile,pluginRoot);ImGui.CloseCurrentPopup();}
        ImGui.SameLine();if(ImGui.Button(L.T("common.cancel"))) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }
}
