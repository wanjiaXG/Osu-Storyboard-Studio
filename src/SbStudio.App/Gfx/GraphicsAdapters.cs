using System.Runtime.InteropServices;

namespace SbStudio.App.Gfx;

/// <summary>A graphics card of this machine that can encode video.</summary>
/// <param name="Maker">"nvidia", "intel" or "amd": which family of encoders goes with it.</param>
/// <param name="Integrated">Built into the processor, as opposed to a card of its own.</param>
public sealed record GraphicsAdapter(string Name, string Maker, bool Integrated);

/// <summary>
/// Finds the graphics cards of the machine, by asking DXGI: that is the list Windows itself goes by, cards
/// without a screen attached included, and without the display drivers of remote-desktop programs.
/// </summary>
public static unsafe class GraphicsAdapters
{
    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDescription
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long Luid;
        public uint Flags;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(Guid* interfaceId, void** factory);

    private const uint SoftwareAdapter = 2;
    private const int NotFound = unchecked((int)0x887A0002);

    private static List<GraphicsAdapter>? found;

    /// <summary>The cards of the makers whose encoders the program can use, in the order Windows lists them. Looked up once.</summary>
    public static IReadOnlyList<GraphicsAdapter> List()
    {
        if (found != null) return found;
        found = new List<GraphicsAdapter>();
        if (!OperatingSystem.IsWindows()) return found;
        try
        {
            // IDXGIFactory1
            var factoryId = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            void* factory = null;
            if (CreateDXGIFactory1(&factoryId, &factory) < 0 || factory == null) return found;
            void** factoryMethods = *(void***)factory;

            for (uint index = 0; index < 16; index++)
            {
                // IDXGIFactory1::EnumAdapters1 is the thirteenth method: after the three of IUnknown, the
                // four of IDXGIObject and the five of IDXGIFactory.
                void* adapter = null;
                int result = ((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)factoryMethods[12])(factory, index, &adapter);
                if (result == NotFound || result < 0 || adapter == null) break;
                void** adapterMethods = *(void***)adapter;

                // IDXGIAdapter1::GetDesc1 is the eleventh: after IUnknown, IDXGIObject and the three of IDXGIAdapter.
                AdapterDescription description;
                if (((delegate* unmanaged[Stdcall]<void*, AdapterDescription*, int>)adapterMethods[10])(adapter, &description) >= 0 &&
                    (description.Flags & SoftwareAdapter) == 0)
                {
                    string name = new string(description.Description).Trim();
                    string? maker = description.VendorId switch
                    {
                        0x10DE => "nvidia",
                        0x8086 => "intel",
                        0x1002 or 0x1022 => "amd",
                        _ => null,
                    };
                    // One card can turn up more than once, as each of the ways it is connected.
                    if (maker != null && !found.Exists(a => a.Name == name))
                        found.Add(new GraphicsAdapter(name, maker, IsIntegrated(maker, name, (ulong)description.DedicatedVideoMemory)));
                }
                ((delegate* unmanaged[Stdcall]<void*, uint>)adapterMethods[2])(adapter);
            }
            ((delegate* unmanaged[Stdcall]<void*, uint>)factoryMethods[2])(factory);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // A Windows without DXGI 1.1 has no card whose encoder could be used either.
        }
        return found;
    }

    /// <summary>
    /// Whether a card is the one in the processor. NVIDIA makes none of those; Intel's are, but for its Arc
    /// cards; AMD makes both, which their names tell apart, and failing that how much memory they own.
    /// </summary>
    private static bool IsIntegrated(string maker, string name, ulong dedicatedMemory)
    {
        if (maker == "nvidia") return false;
        if (maker == "intel") return !name.Contains("Arc", StringComparison.OrdinalIgnoreCase);
        bool card = System.Text.RegularExpressions.Regex.IsMatch(name, @"\bRX\b|\bPro W|FirePro|\bR[579] |\bHD \d{4}|\bXT\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (card) return false;
        if (name.Contains("Graphics", StringComparison.OrdinalIgnoreCase) || name.Contains("Vega", StringComparison.OrdinalIgnoreCase)) return true;
        return dedicatedMemory < 2UL << 30;
    }
}
