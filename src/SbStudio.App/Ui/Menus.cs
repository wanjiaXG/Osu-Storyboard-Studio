using ImGuiNET;
using SbStudio.Core;

namespace SbStudio.App.Ui;

public static class Menus
{
    /// <summary>Actions for a command row. Keep additions here shared by sprite, loop, trigger and group tracks.</summary>
    public static void CommandTrackItems(EditorApp app, System.Collections.IList nodes, CommandKind kind, char parameter = '\0')
    {
        if (UiScriptTargets.Item("timeline.command.delete", ImGui.MenuItem(L.T("edit.delete")))) app.DeleteCommandTrack(nodes, kind, parameter);
    }

    /// <summary>Menu entries that act on the selected objects; shared by the Edit menu and context menus.</summary>
    public static void ObjectItems(EditorApp app)
    {
        bool has = app.Selection.Count > 0 || app.SelectedGroups.Count > 0;
        if (app.Project?.CurrentDifficulty is { } difficulty && app.Selection.Any(app.IsProtectedBeatmapBackground) && ImGui.BeginMenu(L.T("props.owner")))
        {
            if (ImGui.MenuItem(L.T("props.ownerShared")))
                foreach (var obj in app.Selection.Where(app.IsProtectedBeatmapBackground).ToArray()) app.SetOwnDifficulty(obj, false);
            if (ImGui.MenuItem(L.T("props.ownerDifficulty", difficulty.DisplayName)))
                foreach (var obj in app.Selection.Where(app.IsProtectedBeatmapBackground).ToArray()) app.SetOwnDifficulty(obj, true);
            ImGui.EndMenu();
        }
        if (ImGui.MenuItem(L.T("edit.cut"), "Ctrl+X", false, has))
        {
            ImGui.SetClipboardText(app.CopyScript());
            app.ClearKeySelection();
            app.DeleteSelection();
        }
        if (ImGui.MenuItem(L.T("edit.copy"), "Ctrl+C", false, has)) ImGui.SetClipboardText(app.CopyScript());
        if (ImGui.MenuItem(L.T("edit.paste"), "Ctrl+V")) app.PasteScript(ImGui.GetClipboardText(), false);
        if (ImGui.MenuItem(L.T("edit.pasteAtTime"), "Ctrl+Shift+V")) app.PasteScript(ImGui.GetClipboardText(), true);
        if (ImGui.MenuItem(L.T("edit.duplicate"), "Ctrl+D", false, has)) app.DuplicateSelection();
        if (ImGui.MenuItem(L.T("edit.delete"), "Delete", false, has || app.SelectedCommands.Count > 0 || app.SelectedKeys.Count > 0)) app.DeleteSelection();
        ImGui.Separator();
        if (ImGui.MenuItem(L.T("edit.group"), "Ctrl+G", false, CanGroup(app))) app.GroupSelection();
        ImGui.Separator();
        if (ImGui.MenuItem(L.T("edit.toFront"), "Ctrl+Shift+]", false, has)) app.MoveSelectionToEdge(true);
        if (ImGui.MenuItem(L.T("edit.forward"), "Ctrl+]", false, has)) app.MoveSelection(1);
        if (ImGui.MenuItem(L.T("edit.backward"), "Ctrl+[", false, has)) app.MoveSelection(-1);
        if (ImGui.MenuItem(L.T("edit.toBack"), "Ctrl+Shift+[", false, has)) app.MoveSelectionToEdge(false);
    }

    /// <summary>Whether there is something selected that can be put into a group: a sprite, or a group.</summary>
    public static bool CanGroup(EditorApp app) => app.SelectedGroups.Count > 0 || app.Selection.Any(o => o is SbSprite && !app.IsProtectedBeatmapBackground(o));

    /// <summary>Menu entries for one group of sprites, and under them those for whatever is selected with it.</summary>
    public static void GroupItems(EditorApp app, SbObjectGroup group)
    {
        if (group.Plugin==null && ImGui.MenuItem(L.T("group.enter"))) app.OpenContainer(group);
        ImGui.Separator();
        ObjectItems(app);
        ImGui.Separator();
        if(group.Plugin!=null) {if(ImGui.MenuItem(L.T("plugin.detach"))) app.DetachPlugin(group);return;}
        if (ImGui.MenuItem(L.T("group.ungroupKeep"), "Ctrl+Shift+G")) app.Ungroup(group, true);
        if (ImGui.IsItemHovered()) Theme.SetTooltip(L.T("group.ungroupKeep.tip"));
        if (ImGui.MenuItem(L.T("group.ungroupDiscard"))) app.Ungroup(group, false);
        if (ImGui.IsItemHovered()) Theme.SetTooltip(L.T("group.ungroupDiscard.tip"));
    }
}
