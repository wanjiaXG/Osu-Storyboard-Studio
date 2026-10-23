using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace SbStudio.App;

/// <summary>
/// A project double-clicked while the program is already open goes to that window instead of a second copy:
/// the second copy passes the path over a named pipe of the same per-folder name as the instance mutex and
/// quits; the first opens it on its next frame, as if the file had been dropped on it.
/// </summary>
internal sealed class ProjectHandOff : IDisposable
{
    private readonly string pipeName;
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentQueue<string> received = new();

    public ProjectHandOff(string mutexName)
    {
        pipeName = PipeName(mutexName);
        Task.Run(Listen);
    }

    public static string PipeName(string mutexName) => mutexName.Replace("Local\\", "") + "-open";

    /// <summary>A path sent by another copy since the last call, or null.</summary>
    public string? Next() => received.TryDequeue(out string? path) ? path : null;

    private async Task Listen()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(stop.Token);
                using var reader = new StreamReader(server, Encoding.UTF8);
                string? path = await reader.ReadLineAsync(stop.Token);
                if (!string.IsNullOrWhiteSpace(path)) received.Enqueue(path);
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { /* The other copy went away mid-message; wait for the next one. */ }
        }
    }

    /// <summary>Sends a path to the copy that is already open. False when there is none to take it.</summary>
    public static bool Send(string mutexName, string path)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName(mutexName), PipeDirection.Out);
            client.Connect(2000);
            // The open copy may then bring its window to the front, which Windows otherwise forbids it.
            if (OperatingSystem.IsWindows()) AllowSetForegroundWindow(-1);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.WriteLine(Path.GetFullPath(path));
            writer.Flush();
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        // Not disposed: the listener may still be looking at its token.
        stop.Cancel();
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
