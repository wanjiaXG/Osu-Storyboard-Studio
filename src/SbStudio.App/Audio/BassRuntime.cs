using ManagedBass;
using SbStudio.Core;

namespace SbStudio.App.Audio;

public sealed record AudioDevice(int Index, string Id, string Name, bool IsDefault);

/// <summary>Process-wide BASS devices. Streams own their handles, never the shared devices.</summary>
internal static class BassRuntime
{
    private static readonly object gate = new();
    private static bool decodingReady;
    public static IReadOnlyList<AudioDevice> Devices()
    {
        lock (gate)
        {
            var result = new List<AudioDevice>();
            for (int index = 1; Bass.GetDeviceInfo(index, out DeviceInfo info); index++)
            {
                // BASS also exposes a virtual "Default" endpoint. The editor has its own
                // default choice and tracks the physical default endpoint when it changes.
                if (OperatingSystem.IsWindows() && string.IsNullOrEmpty(info.Driver) && info.Name == "Default") continue;
                if (info.IsEnabled) result.Add(new(index, string.IsNullOrEmpty(info.Driver) ? info.Name : info.Driver, info.Name, info.IsDefault));
            }
            return result;
        }
    }

    internal static AudioDevice? ResolveDevice(IReadOnlyList<AudioDevice> devices, string id) =>
        (id.Length > 0 ? devices.FirstOrDefault(d => d.Id == id) : null)
        ?? devices.FirstOrDefault(d => d.IsDefault) ?? devices.FirstOrDefault();

    public static void PrepareDecoding()
    {
        lock (gate)
        {
            if (!decodingReady)
            {
                if (!Bass.Init(Bass.NoSoundDevice) && Bass.LastError != Errors.Already) throw Failure();
                decodingReady = true;
            }
            // Device selection is thread local. Background waveform analysis and exports do not
            // need a speaker and remain valid if playback's device is unavailable.
            Bass.CurrentDevice = Bass.NoSoundDevice;
        }
    }

    public static int PreparePlayback(int device = -1)
    {
        lock (gate)
        {
            if (device < 0) device = ResolveDevice(Devices(), "")?.Index ?? -1;
            // A shared device buffer is still required by the OS. Request a short one; BASS/the
            // driver may round this up to the supported period. Never reinitialize a playing device.
            Bass.DeviceBufferLength = 10;
            if (!Bass.Init(device, AudioEngine.SampleRate, DeviceInitFlags.Latency) && Bass.LastError != Errors.Already) throw Failure();
            if (device > 0) Bass.CurrentDevice = device;
            return Bass.CurrentDevice;
        }
    }

    public static InvalidOperationException Failure() => new(L.T("audio.bassFailed", Bass.LastError.ToString()));
}
