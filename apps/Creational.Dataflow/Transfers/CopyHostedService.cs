using System.Threading.Channels;

namespace Creational.Dataflow.Transfers;

public sealed class CopyHostedService(IStreamingCopier copier, ILogger<CopyHostedService> logger,
    IConfiguration configuration) : BackgroundService
{
    private readonly Channel<CopyRequest> _queue = Channel.CreateBounded<CopyRequest>(
        new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.Wait, SingleReader = false });
    private readonly SemaphoreSlim _imageSource = new(1, 1);

    public async Task<CopyResult> EnqueueAsync(CopyTarget target, CancellationToken cancellationToken)
    {
        var request = new CopyRequest(target);
        await _queue.Writer.WriteAsync(request, cancellationToken);
        return await request.Completion.Task.WaitAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var parallelism = Math.Clamp(configuration.GetValue("Copies:Parallelism", 5), 1, 5);
        await Task.WhenAll(Enumerable.Range(0, parallelism).Select(_ => WorkAsync(stoppingToken)));
    }

    private async Task WorkAsync(CancellationToken ct)
    {
        await foreach (var request in _queue.Reader.ReadAllAsync(ct))
        {
            var image = request.Target.SourceUrl.Contains("upload.wikimedia.org", StringComparison.OrdinalIgnoreCase);
            try
            {
                if (image) await _imageSource.WaitAsync(ct);
                try { request.Completion.TrySetResult(await copier.CopyAsync(request.Target, ct)); }
                finally { if (image) _imageSource.Release(); }
            }
            catch (Exception error)
            {
                logger.LogError(error, "Copy failed for {Target}", request.Target.Id);
                request.Completion.TrySetException(error);
            }
        }
    }

    private sealed record CopyRequest(CopyTarget Target)
    {
        public TaskCompletionSource<CopyResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
