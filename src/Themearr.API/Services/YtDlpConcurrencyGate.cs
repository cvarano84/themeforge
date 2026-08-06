namespace Themearr.API.Services;

public sealed class YtDlpConcurrencyGate(DownloaderConfiguration configuration)
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _changed = new(0);
    private int _active;

    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        while (true)
        {
            lock (_sync)
            {
                if (_active < configuration.GetSnapshot().ConcurrentDownloads)
                {
                    _active++;
                    return new Lease(this);
                }
            }
            await _changed.WaitAsync(TimeSpan.FromSeconds(1), ct);
        }
    }

    private void Release()
    {
        lock (_sync) _active--;
        try { _changed.Release(); } catch (SemaphoreFullException) { }
    }

    private sealed class Lease(YtDlpConcurrencyGate owner) : IDisposable
    {
        private YtDlpConcurrencyGate? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
