using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NetRelay.Services;

public sealed class SingleInstanceService : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private Task? _listenerTask;
    private bool _ownsMutex;

    public SingleInstanceService(string scopeName)
    {
        _mutex = new Mutex(false, $@"Local\{scopeName}-Mutex");
        // Ensure pipe name has no invalid characters
        _pipeName = $"NetRelay-IPC-{scopeName.Replace("{", "").Replace("}", "").Replace("-", "")}";
    }

    public bool TryAcquire()
    {
        try
        {
            _ownsMutex = _mutex.WaitOne(0, false);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }

        return _ownsMutex;
    }

    public void SignalPrimaryInstance(string[] args)
    {
        try
        {
            using var pipeClient = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            pipeClient.Connect(1000); // Wait up to 1 second
            
            using var writer = new StreamWriter(pipeClient);
            var json = JsonSerializer.Serialize(args);
            writer.WriteLine(json);
            writer.Flush();
        }
        catch
        {
            // Ignore transmission errors
        }
    }

    public void StartListening(Action<string[]> activationAction)
    {
        _listenerTask = Task.Run(async () =>
        {
            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                NamedPipeServerStream? pipeServer = null;
                try
                {
                    pipeServer = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await pipeServer.WaitForConnectionAsync(_cancellationTokenSource.Token);

                    using var reader = new StreamReader(pipeServer);
                    var message = await reader.ReadLineAsync(_cancellationTokenSource.Token);
                    if (!string.IsNullOrEmpty(message))
                    {
                        try
                        {
                            var args = JsonSerializer.Deserialize<string[]>(message);
                            if (args != null)
                            {
                                activationAction(args);
                            }
                        }
                        catch
                        {
                            // Ignore malformed JSON messages
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Delay slightly on generic errors before recreating pipe server
                    await Task.Delay(100);
                }
                finally
                {
                    pipeServer?.Dispose();
                }
            }
        });
    }

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
        try
        {
            // Connect dummy client to release the blocked server wait if needed
            using (var dummy = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out))
            {
                dummy.Connect(50);
            }
        }
        catch
        {
            // Suppress connection failure when server is already stopped
        }

        try
        {
            _listenerTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Ignore thread join errors during shutdown
        }

        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // Suppress release errors
            }
            _ownsMutex = false;
        }

        _mutex.Dispose();
        _cancellationTokenSource.Dispose();
    }
}
