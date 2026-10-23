using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using SbStudio.Effects;

namespace SbStudio.App.Effects;

internal sealed class EffectHostPool : IDisposable
{
    private readonly SemaphoreSlim capacity;
    private readonly ConcurrentBag<Worker> idle = [];
    private readonly ConcurrentDictionary<Worker, byte> workers = new();
    private readonly string executable;
    private readonly object lifetimeGate = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly HashSet<Task> running = [];
    private bool disposed;
    internal TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    internal EffectHostPool(string? executable = null, int concurrency = 2)
    { this.executable = executable ?? Path.Combine(AppContext.BaseDirectory, "effects", "host", "SbStudio.Effects.Host.exe"); capacity = new(Math.Clamp(concurrency, 1, 8)); }
    internal Task<HostResponse> RunAsync(HostRequest request, CancellationToken token, Action<HostResponse>? progress = null)
    {
        lock (lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var task = RunCoreAsync(request, token, progress); running.Add(task);
            _ = task.ContinueWith(done => { lock (lifetimeGate) running.Remove(done); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }
    private async Task<HostResponse> RunCoreAsync(HostRequest request, CancellationToken caller, Action<HostResponse>? progress)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, shutdown.Token);
        var token = linked.Token;
        await capacity.WaitAsync(token);
        Worker? worker = null;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            string revision = await Task.Run(() => request.Development
                ? DevelopmentSnapshot.Capture(request.SourceDirectory, token).Revision
                : PackageFiles.ValidateDirectory(request.SourceDirectory).ContentHash, token);
            request = request with { Identity = request.Identity with { CodeRevision = revision } };
            while (idle.TryTake(out var existing))
            {
                if (existing.Alive && existing.Revision == Identity(request)) { worker = existing; break; }
                workers.TryRemove(existing, out _); existing.Dispose();
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(Timeout);
            worker ??= await Worker.Start(executable, Identity(request), deadline.Token); workers.TryAdd(worker, 0);
            await HostWire.WriteAsync(worker.Pipe, request, deadline.Token);
            while (true)
            {
                var result = await HostWire.ReadAsync<HostResponse>(worker.Pipe, deadline.Token);
                if (result.ProtocolVersion != 1 || result.RequestId != request.RequestId || result.Identity != request.Identity)
                    throw new IOException("Mismatched effect response.");
                if (result.Kind is HostMessageKind.Progress or HostMessageKind.Diagnostic) { progress?.Invoke(result); continue; }
                if (result.Kind is not (HostMessageKind.Completed or HostMessageKind.Failed))
                    throw new IOException("Unexpected effect response.");
                worker.Requests++;
                return result;
            }
        }
        catch (OperationCanceledException)
        {
            if (worker != null) { workers.TryRemove(worker, out _); await worker.Cancel(request.RequestId); worker.Dispose(); worker = null; }
            if (token.IsCancellationRequested) throw;
            return new() { RequestId = request.RequestId, Identity = request.Identity, Kind = HostMessageKind.Failed, Error = "Effect execution timed out." };
        }
        catch
        {
            if (worker != null) { workers.TryRemove(worker, out _); worker.Dispose(); worker = null; }
            throw;
        }
        finally
        {
            // A module may retain threads or static references after Unload. Bound even same-version reuse.
            if (worker != null)
            {
                if (disposed || worker.Requests >= 16) { workers.TryRemove(worker, out _); worker.Dispose(); }
                else idle.Add(worker);
            }
            capacity.Release();
        }
    }
    private static string Identity(HostRequest request) => Path.GetFullPath(request.SourceDirectory) + "|" + request.Development + "|" + request.Identity.CodeRevision;
    public void Dispose()
    {
        Task[] pending;
        lock (lifetimeGate) { if (disposed) return; disposed = true; pending = running.ToArray(); }
        shutdown.Cancel();
        foreach (var worker in workers.Keys) worker.Dispose(); workers.Clear();
        try { Task.WhenAll(pending).Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        while (idle.TryTake(out _)) { }
    }
    private sealed class Worker : IDisposable
    {
        private readonly Process process;
        private readonly WindowsJob job;
        internal readonly NamedPipeServerStream Pipe;
        internal readonly string Revision;
        internal int Requests;
        private int closed;
        internal bool Alive { get { try { return closed == 0 && !process.HasExited && Pipe.IsConnected; } catch { return false; } } }
        private Worker(Process process, WindowsJob job, NamedPipeServerStream pipe, string revision)
        { this.process = process; this.job = job; Pipe = pipe; Revision = revision; }
        internal static async Task<Worker> Start(string executable, string revision, CancellationToken token)
        {
            string name = "SbStudio.Effects." + Guid.NewGuid().ToString("N");
            var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(name);
            var job = new WindowsJob(); Process? process = null;
            try
            {
                process = Process.Start(start) ?? throw new IOException("Effect host did not start."); job.Assign(process);
                _ = Drain(process.StandardOutput); _ = Drain(process.StandardError);
                await pipe.WaitForConnectionAsync(token); var hello = await HostWire.ReadAsync<HostResponse>(pipe, token);
                if (!hello.Success || hello.ProtocolVersion != 1 || hello.RequestId != "Handshake") throw new IOException("Invalid effect host handshake.");
                return new(process, job, pipe, revision);
            }
            catch { pipe.Dispose(); job.Dispose(); if (process != null) { try { process.Kill(true); } catch { } process.Dispose(); } throw; }
        }
        private static async Task Drain(StreamReader reader)
        {
            // Discard console output in fixed chunks, including lines with no newline.
            var buffer = new char[4096];
            try { while (await reader.ReadAsync(buffer) != 0) { } }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            Pipe.Dispose(); job.Dispose();
            try { if (!process.HasExited) process.Kill(true); process.WaitForExit(3000); } catch (InvalidOperationException) { }
            process.Dispose();
        }
        internal async Task Cancel(string requestId)
        {
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                await HostWire.WriteAsync(Pipe, new HostRequest { Operation = "Cancel", RequestId = requestId }, grace.Token);
                await process.WaitForExitAsync(grace.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException) { }
        }
    }
}

internal sealed class WindowsJob : IDisposable
{
    private nint handle;
    internal WindowsJob()
    {
        if (!OperatingSystem.IsWindows()) return;
        handle = CreateJobObjectW(0, null);
        if (handle == 0) throw new System.ComponentModel.Win32Exception();
        var limits = new ExtendedLimit { Basic = new BasicLimit { Flags = 0x2000 } };
        if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimit>())) { Dispose(); throw new System.ComponentModel.Win32Exception(); }
    }
    internal void Assign(Process process)
    { if (handle != 0 && !AssignProcessToJobObject(handle, process.Handle)) throw new System.ComponentModel.Win32Exception(); }
    public void Dispose() { nint h = Interlocked.Exchange(ref handle, 0); if (h != 0) CloseHandle(h); }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimit
    { public long ProcessTime, JobTime; public uint Flags; public nuint Minimum, Maximum; public uint ProcessCount; public nuint Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit { public BasicLimit Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcess, PeakJob; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(nint job, int info, ref ExtendedLimit limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
