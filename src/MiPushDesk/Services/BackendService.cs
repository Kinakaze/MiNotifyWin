using MiPushDesk.Core;
using MiPushDesk.Core.Protocol;

namespace MiPushDesk.Services;

public sealed class BackendService(AppPaths paths, AppStore store) : IAsyncDisposable
{
    private MiPushReceiver? _receiver;
    private CancellationTokenSource? _stop;
    private Task? _running;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public bool IsRunning => _running is { IsCompleted: false };
    public bool RequestAppSecretRecovery() => IsRunning && _receiver?.RequestAppSecretRecovery() == true;
    public async Task StartAsync(int heartbeat, int reconnect)
    {
        await _gate.WaitAsync();
        try
        {
            if (IsRunning) return;
            _stop?.Dispose();
            _stop = new();
            _receiver = new(paths, store, new() { Heartbeat = TimeSpan.FromSeconds(heartbeat), Reconnect = TimeSpan.FromSeconds(reconnect) });
            var receiver = _receiver;
            var cancellation = _stop.Token;
            _running = Task.Run(() => receiver.RunAsync(cancellation));
            await Task.Delay(80);
            if (_running.IsCompleted) await _running;
        }
        finally { _gate.Release(); }
    }
    public void UpdateTiming(int heartbeat, int reconnect)
    {
        _receiver?.UpdateTiming(TimeSpan.FromSeconds(heartbeat), TimeSpan.FromSeconds(reconnect));
    }
    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_running is null) return;
            _stop!.Cancel();
            try { await _running; }
            finally { _running = null; _stop.Dispose(); _stop = null; }
        }
        finally { _gate.Release(); }
    }
    public ListenerState ReadState()
    {
        if (_running?.IsFaulted == true) return new() { State = "failed", LastError = _running.Exception!.GetBaseException().GetType().Name };
        return _receiver?.Snapshot() ?? new();
    }
    public async ValueTask DisposeAsync() => await StopAsync();
}
