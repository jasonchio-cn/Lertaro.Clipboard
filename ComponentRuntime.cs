namespace Lertaro.Plugins.Clipboard;

// Serialize start/stop across rapid settings changes and a terminal host shutdown.
internal sealed class ComponentRuntime(Func<bool> enabled, Func<Task> start, Func<Task> stop)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _active, _closed;

    internal Task RefreshAsync() => TransitionAsync(false);
    internal Task StopAsync() => TransitionAsync(true);

    private async Task TransitionAsync(bool close)
    {
        await _gate.WaitAsync();
        try
        {
            if (close) _closed = true;
            if (!_closed && enabled() && !_active)
            {
                try { await start(); _active = true; }
                catch { await stop(); throw; }
            }
            // Recheck after an asynchronous start: the user may have disabled it meanwhile.
            if ((_closed || !enabled()) && _active)
            {
                await stop();
                _active = false;
            }
        }
        finally { _gate.Release(); }
    }
}
