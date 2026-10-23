using System.IO.Pipes;
using System.Threading.Channels;
using SbStudio.Effects;
using SbStudio.Effects.Host;

if (args.Length == 3 && args[0] == "--prepare-package")
{
    var response = EffectRunner.Run(new() { Operation = "Prepare", SourceDirectory = args[1], WorkDirectory = args[2] }, CancellationToken.None);
    if (!response.Success) { Console.Error.WriteLine(response.Error); return 1; }
    return 0;
}
if (args.Length != 2 || args[0] != "--pipe") return 2;
using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
using var lifetime = new CancellationTokenSource();
await pipe.ConnectAsync(10000, lifetime.Token);
await HostWire.WriteAsync(pipe, new HostResponse { RequestId = "Handshake", Kind = HostMessageKind.Handshake, Success = true });
try
{
    Task<HostRequest> incoming = HostWire.ReadAsync<HostRequest>(pipe, lifetime.Token);
    while (pipe.IsConnected)
    {
        var request = await incoming.WaitAsync(TimeSpan.FromSeconds(60));
        if (request.Operation == "Shutdown") break;
        incoming = HostWire.ReadAsync<HostRequest>(pipe, lifetime.Token);
        using var cancellation = new CancellationTokenSource();
        var messages = Channel.CreateBounded<HostResponse>(256);
        var writing = Task.Run(async () =>
        {
            await foreach (var message in messages.Reader.ReadAllAsync(lifetime.Token))
                await HostWire.WriteAsync(pipe, message, lifetime.Token);
        });
        var running = Task.Run(() => EffectRunner.Run(request, cancellation.Token, message => messages.Writer.TryWrite(message)));
        bool cancelled = false;
        if (await Task.WhenAny(running, incoming) == incoming)
        {
            var control = await incoming;
            if (control.Operation != "Cancel" || control.RequestId != request.RequestId) throw new IOException("Unexpected effect request while busy.");
            cancellation.Cancel(); cancelled = true;
        }
        var response = await running;
        await messages.Writer.WriteAsync(response, lifetime.Token);
        messages.Writer.Complete();
        await writing;
        if (cancelled) break;
    }
}
catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException) { }
finally { lifetime.Cancel(); }
return 0;
