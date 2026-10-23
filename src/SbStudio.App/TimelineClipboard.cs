using System.Runtime.InteropServices;
using SbStudio.App.Ui;

namespace SbStudio.App;

/// <summary>Consumes each clipboard revision once; a busy clipboard is retried on the next poll.</summary>
internal sealed class TimelineClipboard
{
    private bool active;
    private uint sequence;

    public int? Poll(bool enabled, uint revision, Func<string?> read)
    {
        if (!enabled || !active)
        {
            active = enabled;
            sequence = revision;
            return null;
        }
        if (revision == sequence) return null;
        string? text = read();
        if (text == null) return null;
        sequence = revision;
        return OsuEditorTimestamp.TryParse(text, out int time) ? time : null;
    }

    [DllImport("user32.dll")]
    internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();
    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")]
    private static extern nuint GlobalSize(IntPtr handle);

    internal static string? ReadText()
    {
        const uint unicodeText = 13;
        if (!IsClipboardFormatAvailable(unicodeText)) return "";
        if (!OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            IntPtr handle = GetClipboardData(unicodeText);
            if (handle == IntPtr.Zero) return null;
            nuint bytes = GlobalSize(handle);
            if (bytes is < 2 or > 8194) return "";
            IntPtr data = GlobalLock(handle);
            if (data == IntPtr.Zero) return null;
            try { return (Marshal.PtrToStringUni(data, (int)(bytes / 2)) ?? "").Split('\0')[0]; }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }
}
