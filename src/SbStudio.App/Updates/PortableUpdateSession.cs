using System.Diagnostics;
using SbStudio.Core.Updates;

namespace SbStudio.App.Updates;

/// <summary>Background I/O only. UI state is consumed on the render thread.</summary>
internal sealed class PortableUpdateSession : IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(20) };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private float fraction;
    public float Fraction => Volatile.Read(ref fraction);
    public string? Work { get; private set; }
    public bool CanInstall => File.Exists(Path.Combine(AppContext.BaseDirectory, PortablePackage.ManifestName)) &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, PortablePackage.UpdaterExe));

    public async Task<UpdateRelease?> CheckAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        return await new GithubUpdates(http).CheckAsync(EditorApp.Version, timeout.Token);
    }

    public async Task PrepareAsync(UpdateRelease release)
    {
        operation?.Dispose();
        operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var cancel = operation.Token;
        fraction = 0;
        Work = Path.Combine(AppSettings.Folder, "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Work);
        string zip = Path.Combine(Work, GithubUpdates.AssetName);
        await new GithubUpdates(http).DownloadAsync(release, zip, new DownloadProgress(this), cancel);
        await Task.Run(() => PortablePackage.Extract(zip, Path.Combine(Work, "prepared"), release.Version, cancel), cancel);
        File.Delete(zip);
    }

    public void Cancel() => operation?.Cancel();

    public async Task StartHelperAsync(string language)
    {
        string work = Work ?? throw new InvalidOperationException("No prepared update.");
        string helper = Path.Combine(work, "OsuStoryboardStudio.Updater.exe");
        foreach (string marker in new[] { "ready", "error.txt" })
            if (File.Exists(Path.Combine(work, marker))) File.Delete(Path.Combine(work, marker));
        // Run the current, trusted updater outside the directory it is going to replace.
        File.Copy(Path.Combine(AppContext.BaseDirectory, PortablePackage.UpdaterExe), helper, true);
        using var self = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = work };
        foreach (string argument in new[] { "--apply", AppContext.BaseDirectory, work, self.Id.ToString(),
                     self.StartTime.ToUniversalTime().Ticks.ToString(), language }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Unable to start the updater.");
        // The helper confirms checksums and permissions before the editor commits to exiting.
        for (int i = 0; i < 600; i++)
        {
            if (File.Exists(Path.Combine(work, "error.txt"))) throw new IOException(File.ReadAllText(Path.Combine(work, "error.txt")));
            if (File.Exists(Path.Combine(work, "ready"))) return;
            if (process.HasExited) throw new IOException(File.Exists(Path.Combine(work, "error.txt"))
                ? File.ReadAllText(Path.Combine(work, "error.txt")) : "The updater exited before it was ready.");
            await Task.Delay(100, lifetime.Token);
        }
        // Never let a late helper apply an update after a reported startup failure.
        if (!process.HasExited) process.Kill();
        throw new TimeoutException("The updater did not become ready.");
    }

    private sealed class DownloadProgress(PortableUpdateSession owner) : IProgress<float>
    {
        public void Report(float value) => Volatile.Write(ref owner.fraction, value);
    }

    public void Dispose()
    {
        lifetime.Cancel();
        operation?.Cancel();
        http.Dispose();
        // Tasks may still be observing the tokens; do not dispose their sources underneath them.
    }
}
