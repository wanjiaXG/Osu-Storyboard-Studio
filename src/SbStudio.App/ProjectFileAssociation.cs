using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using SbStudio.Core;

namespace SbStudio.App;

/// <summary>
/// Makes a double-click on a .sbproj file open it here: the file type is registered for the current user only
/// (HKCU\Software\Classes, no administrator rights), pointing at this exe. A portable folder can be moved, so
/// the registration is checked at every normal start and rewritten when it names another place.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ProjectFileAssociation
{
    public const string Extension = ".sbproj";
    public const string ProgId = "OsuStoryboardStudio.Project";
    private const string ClassesPath = @"Software\Classes";

    public static string Command(string exe) => $"\"{exe}\" \"%1\"";

    /// <summary>Registers or removes the association as the setting says. Returns whether anything was written.</summary>
    public static bool Apply(bool enabled, string exe) => Apply(enabled, exe, Registry.CurrentUser, ClassesPath, notify: true);

    internal static bool Apply(bool enabled, string exe, RegistryKey root, string classesPath, bool notify = false)
    {
        using var classes = root.CreateSubKey(classesPath);
        bool changed = enabled ? Register(classes, exe) : Unregister(classes);
        // Explorer reads associations once; this tells it to look again, so icons and double-clicks follow at once.
        if (changed && notify) SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        return changed;
    }

    public static bool IsRegistered(RegistryKey classes, string exe)
    {
        using var extension = classes.OpenSubKey(Extension);
        using var command = classes.OpenSubKey($@"{ProgId}\shell\open\command");
        return extension?.GetValue("") as string == ProgId && command?.GetValue("") as string == Command(exe);
    }

    private static bool Register(RegistryKey classes, string exe)
    {
        if (IsRegistered(classes, exe)) return false;
        using (var extension = classes.CreateSubKey(Extension)) extension.SetValue("", ProgId);
        using (var type = classes.CreateSubKey(ProgId)) type.SetValue("", "osu! Storyboard Studio " + L.T("fileType.project"));
        using (var icon = classes.CreateSubKey($@"{ProgId}\DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
        using (var command = classes.CreateSubKey($@"{ProgId}\shell\open\command")) command.SetValue("", Command(exe));
        return true;
    }

    /// <summary>Removes only what this program wrote: an .sbproj taken over by another program is left alone.</summary>
    private static bool Unregister(RegistryKey classes)
    {
        bool changed = false;
        using (var extension = classes.OpenSubKey(Extension, writable: true))
        {
            if (extension?.GetValue("") as string == ProgId)
            {
                extension.DeleteValue("");
                changed = true;
            }
        }
        if (classes.OpenSubKey(ProgId) is { } type)
        {
            type.Dispose();
            classes.DeleteSubKeyTree(ProgId);
            changed = true;
        }
        if (classes.OpenSubKey(Extension) is { } left)
        {
            bool empty = left.ValueCount == 0 && left.SubKeyCount == 0;
            left.Dispose();
            if (empty) classes.DeleteSubKey(Extension);
        }
        return changed;
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
