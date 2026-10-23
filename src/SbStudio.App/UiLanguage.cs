using System.Runtime.InteropServices;
using SbStudio.Core;

namespace SbStudio.App;

/// <summary>Chooses the interface language: the one in the settings, otherwise the closest to the system's.</summary>
public static class UiLanguage
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetUserDefaultLocaleName(char[] name, int size);

    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int LCIDToLocaleName(uint locale, char[] name, int size, uint flags);

    private static string? systemCode;

    /// <summary>The available language closest to the system's.</summary>
    public static string SystemCode => systemCode ??= Detect();

    /// <param name="setting">A language code, or empty to follow the system.</param>
    public static void Apply(string? setting) => L.Use(L.Match(setting) ?? SystemCode);

    private static string Detect()
    {
        // Globalization is switched off in this program, so CultureInfo knows nothing: ask the system.
        // The regional format comes first: it says which language the user reads even when Windows
        // itself is displayed in another one.
        foreach (string locale in SystemLocales())
            if (L.Match(locale) is { } code) return code;
        return L.FallbackCode;
    }

    private static IEnumerable<string> SystemLocales()
    {
        if (OperatingSystem.IsWindows())
        {
            var buffer = new char[85];
            int length = GetUserDefaultLocaleName(buffer, buffer.Length);
            if (length > 1) yield return new string(buffer, 0, length - 1);
            length = LCIDToLocaleName(GetUserDefaultUILanguage(), buffer, buffer.Length, 0);
            if (length > 1) yield return new string(buffer, 0, length - 1);
        }
        else
        {
            // "zh_CN.UTF-8" style.
            string? lang = Environment.GetEnvironmentVariable("LC_ALL") ?? Environment.GetEnvironmentVariable("LANG");
            if (!string.IsNullOrEmpty(lang)) yield return lang.Split('.')[0];
        }
    }
}
