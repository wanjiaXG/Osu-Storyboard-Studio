using System.Numerics;
using ImGuiNET;
using SbStudio.Core;

namespace SbStudio.App.Ui;

/// <summary>
/// The plugins of the project at a glance: every plugin group with its state, and what can be done with
/// it. A click selects the group, as a click on its row in the timeline does.
/// </summary>
public sealed class PluginsPanel(EditorApp app)
{
    private readonly EffectsPanel library = new(app);
    internal bool EffectsBusy => library.Busy;
    public const string WindowId = "###plugins";
    public bool Open = true;
    public uint DockBeside;
    /// <summary>Asks for the dialog that links an external osb, which the main window owns.</summary>
    public Action? LinkRequested;

    public void Draw()
    {
        if (!Open) return;
        ImGui.SetNextWindowSize(new Vector2(Theme.Px(320), Theme.Px(360)), ImGuiCond.FirstUseEver);
        if (DockBeside != 0) ImGui.SetNextWindowDockID(DockBeside, ImGuiCond.FirstUseEver);
        if (!ImGui.Begin(L.T("panel.plugins") + WindowId, ref Open, ImGuiWindowFlags.NoFocusOnAppearing))
        {
            ImGui.End();
            return;
        }

        if (ImGui.BeginTabBar("##effectTabs"))
        {
            if (UiScriptTargets.Item("library.tab", ImGui.BeginTabItem(L.T("effects.library")))) { library.Draw(); ImGui.EndTabItem(); }
            if (UiScriptTargets.Item("instances.tab", ImGui.BeginTabItem(L.T("effects.instances")))) { DrawInstances(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem(L.T("effects.compatibility"))) { if(ImGui.Button(L.T("menu.plugins.link"))) LinkRequested?.Invoke(); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
        ImGui.End();
    }

    private void DrawInstances()
    {
        var groups = app.Doc.Groups.Where(g => g.Plugin != null && !PluginLink.IsGenerated(g)).ToList();
        ImGui.BeginDisabled(groups.Count == 0);
        if (ImGui.Button(L.T("menu.plugins.reimport"))) app.ReimportPlugins();
        ImGui.EndDisabled();
        ImGui.Separator();

        if (groups.Count == 0) Theme.TextWrapped(L.T("plugin.panel.empty"));
        else if (ImGui.BeginChild("##plugins"))
        {
            foreach (var group in groups) DrawRow(group);
        }
        if (groups.Count > 0) ImGui.EndChild();
    }

    private void DrawRow(SbObjectGroup group)
    {
        var link = group.Plugin!;
        app.Plugins.TryGetValue(link.Instance, out var runtime);
        var status = runtime?.Status ?? PluginStatus.Importing;
        ImGui.PushID(link.Instance);

        // The state first, in its colour, so that a failed one stands out in a long list.
        var cursor = ImGui.GetCursorScreenPos();
        float height = Theme.Px(26);
        bool selected = app.IsSelected(group);
        if (ImGui.Selectable("##row", selected, ImGuiSelectableFlags.AllowOverlap, new Vector2(0, height)))
        {
            app.OpenContainer(group.Parent);
            app.SelectGroup(group);
        }
        UiScriptTargets.Item("instance." + link.Instance, false);
        bool hovered = ImGui.IsItemHovered();
        if (ImGui.BeginPopupContextItem("##menu"))
        {
            if (ImGui.MenuItem(L.T("plugin.reimport"))) app.ReimportPlugin(group);
            if (ImGui.MenuItem(L.T("plugin.openFolder"), null, false, app.SourceOf(link).Supported))
                app.SourceOf(link).Reveal(app.Project!, link);
            ImGui.Separator();
            if (UiScriptTargets.Item("instance.detach", ImGui.MenuItem(L.T("plugin.detach")))) app.DetachPlugin(group);
            if (UiScriptTargets.Item("instance.detachcopy", ImGui.MenuItem(L.T("plugin.detachcopy")))) app.DetachPluginCopy(group);
            ImGui.EndPopup();
        }

        var dl = ImGui.GetWindowDrawList();
        float textY = cursor.Y + (height - ImGui.GetTextLineHeight()) / 2;
        float x = cursor.X + Theme.Px(6);
        dl.AddCircleFilled(new Vector2(x + Theme.Px(4), cursor.Y + height / 2), Theme.Px(4), StatusColor(status));
        x += Theme.Px(16);
        string name = group.Name.Length > 0 ? group.Name : Path.GetFileNameWithoutExtension(link.File);
        dl.AddText(new Vector2(x, textY), ImGui.GetColorU32(ImGuiCol.Text), name);
        string detail = app.PluginStatusText(status)
            + (runtime?.Output is { } output ? "  ·  " + L.T("plugin.panel.sprites", output.SpriteCount) : "");
        float detailWidth = ImGui.CalcTextSize(detail).X;
        float right = cursor.X + ImGui.GetContentRegionAvail().X;
        dl.AddText(new Vector2(MathF.Max(x + ImGui.CalcTextSize(name).X + Theme.Px(12), right - detailWidth - Theme.Px(6)), textY), Theme.TextDim, detail);

        if (hovered)
        {
            string tip = app.SourceOf(link).Description + "\n" + link.File;
            if (runtime?.Error is { Length: > 0 } error) tip += "\n" + error;
            Theme.SetTooltip(tip + "\n" + L.T("plugin.panel.tip"));
        }
        ImGui.PopID();
    }

    private static uint StatusColor(PluginStatus status) => status switch
    {
        PluginStatus.Ready => Theme.Rgba(100, 230, 140),
        PluginStatus.Importing => Theme.Rgba(255, 205, 80),
        PluginStatus.Failed => Theme.Rgba(255, 80, 80),
        _ => Theme.Rgba(150, 150, 150),
    };
}
