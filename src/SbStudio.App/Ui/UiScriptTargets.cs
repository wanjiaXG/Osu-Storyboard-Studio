using System.Numerics;
using ImGuiNET;

namespace SbStudio.App.Ui;

/// <summary>Records visible control geometry for the existing input-script runner.</summary>
internal static class UiScriptTargets
{
    private static readonly Dictionary<string, (Vector2 Point, int Frame)> targets = new();
    internal static bool Item(string name, bool result)
    {
        if (AppSettings.ReadOnly && ImGui.IsItemVisible())
            targets[name] = ((ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) / 2, ImGui.GetFrameCount());
        return result;
    }
    internal static void Position(string name, Vector2 point)
    {
        if (AppSettings.ReadOnly) targets[name] = (point, ImGui.GetFrameCount());
    }
    internal static Vector2 Point(string name)
    {
        if (!targets.TryGetValue(name, out var item) || ImGui.GetFrameCount() - item.Frame > 1)
            throw new InvalidOperationException("Script control is not visible: " + name);
        return item.Point;
    }
}
