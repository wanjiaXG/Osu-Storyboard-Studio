using ImGuiNET;
using SbStudio.Core;

namespace SbStudio.App.Ui;

public sealed partial class PropertiesPanel
{
    private string pluginOffsetText="";
    private int pluginOffsetOwner,pluginOffsetSaved;
    private void DrawPlugin(SbObjectGroup group)
    {
        var link=group.Plugin!;ImGui.SeparatorText(L.T("menu.plugins"));
        var source=app.SourceOf(link);ImGui.TextUnformatted(source.Description);
        app.Plugins.TryGetValue(link.Instance,out var runtime);
        if(source.Supported)
        {
            source.DrawProperties(app,group);
            if(pluginOffsetOwner!=group.Id || pluginOffsetSaved!=link.Offset)
            {pluginOffsetOwner=group.Id;pluginOffsetSaved=link.Offset;pluginOffsetText=link.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture);}
            bool entered=ImGui.InputText(L.T("plugin.offset"),ref pluginOffsetText,32,ImGuiInputTextFlags.EnterReturnsTrue);
            if(entered || ImGui.IsItemDeactivatedAfterEdit())
            {
                if(int.TryParse(pluginOffsetText,out int value)) app.SetPluginOffset(group,value);
                else {app.SetStatus(L.T("plugin.offsetInvalid"));pluginOffsetText=link.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture);}
            }
            var status=runtime?.Status??PluginStatus.Importing;
            ImGui.TextColored(status==PluginStatus.Ready?new System.Numerics.Vector4(.4f,.9f,.55f,1):status==PluginStatus.Failed?new System.Numerics.Vector4(1,.3f,.3f,1):status==PluginStatus.Importing?new System.Numerics.Vector4(1,.8f,.3f,1):new System.Numerics.Vector4(.6f,.6f,.6f,1),app.PluginStatusText(status));
            if(runtime!=null)
            {
                Theme.TextDimmed(L.T("plugin.elapsed",runtime.ElapsedMs));
                if(runtime.Output is {} output)
                {
                    Theme.TextDimmed(L.T("plugin.counts",output.SpriteCount,output.CommandCount));
                    if(ImGui.TreeNode(L.T("plugin.warnings",output.Warnings.Count)))
                    {foreach(string warning in output.Warnings) ImGui.TextWrapped(warning);ImGui.TreePop();}
                }
                if(runtime.Error!=null) {ImGui.PushStyleColor(ImGuiCol.Text,new System.Numerics.Vector4(1,.3f,.3f,1));ImGui.TextWrapped(runtime.Error);ImGui.PopStyleColor();}
                foreach (var diagnostic in runtime.Diagnostics.Take(200))
                {
                    ImGui.TextWrapped(diagnostic.Message);
                    if (link.Effect is { SourceKind: "development" } && diagnostic.File is { Length: > 0 } file)
                    {
                        try
                        {
                            string path = SbStudio.Effects.PackageFiles.SafePath(Effects.EffectPluginSource.Root(app.Project!, link), file.Replace('\\','/'));
                            if (ImGui.SmallButton($"{file}:{diagnostic.Line}:{diagnostic.Column}") && File.Exists(path)) AssetsPanel.OpenInExplorer(path, true);
                        }
                        catch (Exception ex) when (ex is IOException or ArgumentException) { }
                    }
                }
            }
            if(ImGui.Button(L.T("plugin.reimport"))) app.ReimportPlugin(group);
            Theme.Tooltip(L.T("plugin.reimport.tip"));
            ImGui.SameLine();if(ImGui.Button(L.T("plugin.openFolder"))) source.Reveal(app.Project!,link);
            Theme.Tooltip(L.T("plugin.openFolder.tip"));
        }
        else {ImGui.TextUnformatted(link.Head);ImGui.TextWrapped(L.T("plugin.unsupportedNote"));}
        if(ImGui.Button(L.T("plugin.detach"))) app.DetachPlugin(group);
        Theme.Tooltip(L.T("plugin.detach.tip"));
        ImGui.SameLine();if(ImGui.Button(L.T("plugin.detachcopy"))) app.DetachPluginCopy(group);
        Theme.Tooltip(L.T("plugin.detachcopy.tip"));
    }
    internal static void DrawLinkedSource(EditorApp app,SbObjectGroup group)
    {
        var link=group.Plugin!;
        ImGui.TextUnformatted(L.T("plugin.file")+": "+System.IO.Path.GetFileName(link.File));Theme.Tooltip(link.File);
        ImGui.SameLine();if(ImGui.SmallButton(L.T("plugin.change")+"###pluginFile"))
        {
            var result=NativeFileDialogSharp.Dialog.FileOpen("osb",null);
            if(result.IsOk) app.SetPluginPath(group,result.Path,link.Root);
        }
        string root=link.Root??System.IO.Path.GetDirectoryName(LinkedImport.SourceFile(app.Project!,link))!;
        ImGui.TextUnformatted(L.T("plugin.root")+": "+System.IO.Path.GetFileName(root));Theme.Tooltip(root);
        ImGui.SameLine();if(ImGui.SmallButton(L.T("plugin.change")+"###pluginRoot"))
        {
            var result=NativeFileDialogSharp.Dialog.FolderPicker(root);
            if(result.IsOk) app.SetPluginPath(group,link.File,result.Path);
        }
    }
}
