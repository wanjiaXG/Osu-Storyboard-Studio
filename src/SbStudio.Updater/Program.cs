using System.Diagnostics;
using System.Runtime.InteropServices;
using SbStudio.Core;
using SbStudio.Core.Updates;

namespace SbStudio.Updater;

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);

    [STAThread]
    private static int Main(string[] args)
    {
        string? work = null;
        try
        {
            if (args.Length == 2 && args[0] == "--validate-manifest")
            {
                PortablePackage.ReadManifest(File.ReadAllText(args[1]));
                return 0;
            }
            if (args.Length != 6 || args[0] != "--apply") throw new ArgumentException("Invalid updater arguments.");
            string target = Path.GetFullPath(args[1]);
            work = Path.GetFullPath(args[2]);
            string language = args[5];
            L.Use(L.Match(language) ?? L.FallbackCode);
            string prepared = Path.Combine(work, "prepared"), backup = Path.Combine(work, "backup");
            var manifest = PortablePackage.ReadManifest(File.ReadAllText(Path.Combine(prepared, PortablePackage.ManifestName)));
            // Validate again before telling the editor it may exit.
            foreach (var file in manifest.Files)
                if (!PortablePackage.FileHash(PortablePackage.SafePath(prepared, file.Path)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Prepared update checksum mismatch.");
            // Refuse unwritable destinations before the editor closes.
            string probe = PortablePackage.SafePath(target, ".update-write-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe)) { }
            File.Delete(probe);
            Process? parentProcess = null;
            try
            {
                parentProcess = Process.GetProcessById(int.Parse(args[3]));
                // Acquire the process handle before signalling readiness, avoiding a race with editor exit.
                if (parentProcess.StartTime.ToUniversalTime().Ticks != long.Parse(args[4]))
                {
                    parentProcess.Dispose();
                    parentProcess = null;
                }
            }
            catch (ArgumentException) { /* The editor has already exited. */ }
            using (parentProcess)
            {
                File.WriteAllText(Path.Combine(work, "ready"), "ready");
                if (parentProcess != null && !parentProcess.WaitForExit(120000))
                    throw new IOException("The editor did not exit. Close it and try again.");
            }

            using var mutex = new Mutex(false, PortablePackage.MutexName(target));
            bool held;
            try { held = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) throw new IOException("Another editor instance is still running.");
            try
            {
                PortablePackage.Recover(target);
                PortablePackage.Install(prepared, target, backup);
            }
            finally { mutex.ReleaseMutex(); }

            StartEditor(target);
            // Keep the old version and the standalone recovery helper in work/backup.
            return 0;
        }
        catch (Exception ex)
        {
            if (args.Length > 0 && args[0] == "--validate-manifest") return 1;
            string details = ex.ToString();
            if (work != null)
            {
                try { File.WriteAllText(Path.Combine(work, "error.txt"), details); }
                catch (IOException) { }
            }
            if (OperatingSystem.IsWindows() && (work == null || File.Exists(Path.Combine(work, "ready"))))
                MessageBoxW(IntPtr.Zero, L.T("update.helperFailed", ex.Message, work ?? ""), "osu! Storyboard Studio", 0x10);
            return 1;
        }
    }

    private static void StartEditor(string target) => Process.Start(new ProcessStartInfo
    {
        FileName = PortablePackage.SafePath(target, PortablePackage.AppExe),
        WorkingDirectory = target,
        UseShellExecute = false,
    });
}
