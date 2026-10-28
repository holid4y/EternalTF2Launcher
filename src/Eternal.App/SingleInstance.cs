using System.IO.Pipes;
using Eternal.Core.Logging;

namespace Eternal.App;

/// <summary>
/// One launcher per user. A second start sends "show" to the first over a
/// local pipe (a Unix socket on Linux) and exits. If the pipe can't be set
/// up the launcher runs anyway.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private static readonly string PipeName = $"eternal-launcher-{Environment.UserName}";
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance() { }

    public event Action? ActivationRequested;

    public static SingleInstance? TryClaim()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(300);
            using var writer = new StreamWriter(client);
            writer.WriteLine("show");
            writer.Flush();
            return null;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            // nobody listening: we are the first instance
        }

        var instance = new SingleInstance();
        _ = Task.Run(instance.ListenAsync);
        return instance;
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var reader = new StreamReader(server);
                if (await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) == "show")
                    ActivationRequested?.Invoke();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Single-instance listener stopped: {ex.Message}");
                return;
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
