namespace Creational.Dataflow.Images;

public sealed class WikimediaRequestPacer
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _next = DateTimeOffset.MinValue;

    public async Task WaitAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var delay = _next - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            _next = DateTimeOffset.UtcNow.AddSeconds(1);
        }
        finally { _gate.Release(); }
    }
}
