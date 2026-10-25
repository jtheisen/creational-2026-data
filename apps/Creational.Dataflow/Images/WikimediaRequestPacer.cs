namespace Creational.Dataflow.Images;

public sealed class WikimediaRequestPacer
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _next = DateTimeOffset.MinValue;
    private int _minimumMilliseconds = 1000;

    public void SetMinimumInterval(TimeSpan interval)
    {
        if (interval < TimeSpan.Zero || interval > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(interval));
        Volatile.Write(ref _minimumMilliseconds, (int)interval.TotalMilliseconds);
    }

    public async Task WaitAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var delay = _next - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            _next = DateTimeOffset.UtcNow.AddMilliseconds(Volatile.Read(ref _minimumMilliseconds));
        }
        finally { _gate.Release(); }
    }
}
