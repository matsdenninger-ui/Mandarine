using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Mandarine;

/// <summary>
/// Keeps one running instance per user. Later launches (e.g. from the "Send to" menu)
/// hand their command line to the running instance through a named pipe and exit.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    readonly Mutex _mutex;
    readonly string _pipeName;
    readonly CancellationTokenSource _cts = new();

    public bool IsFirst { get; }

    /// <summary>Raised on a thread-pool thread.</summary>
    public event Action<string[]>? ArgumentsReceived;

    public SingleInstance(string id)
    {
        var user = Environment.UserName;
        _mutex = new Mutex(true, $@"Local\{id}.{user}", out bool created);
        IsFirst = created;
        _pipeName = PipeName(id);
    }

    static string PipeName(string id) => $"{id}.{Environment.UserName}.pipe";

    public void StartServer() => Task.Run(ServerLoopAsync);

    async Task ServerLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var text = await reader.ReadToEndAsync(_cts.Token).ConfigureAwait(false);
                var args = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                ArgumentsReceived?.Invoke(args);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error("Pipe server", ex);
                try { await Task.Delay(250, _cts.Token).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    public static bool SendToFirst(string id, string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName(id), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.Write(string.Join('\n', args.Length == 0 ? new[] { "--activate" } : args));
            writer.Flush();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not reach running instance", ex);
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { if (IsFirst) _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
    }
}
