using Microsoft.Extensions.Hosting;

namespace Creational.Dataflow.Images;

public sealed class ThumbnailImportService(
    IThumbnailImportRepository repository,
    IThumbnailBlobCopier copier,
    WikimediaImageInfo metadata,
    ILogger<ThumbnailImportService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    public async Task<string> CreateJobAsync(IReadOnlyList<string> titles, CancellationToken ct)
    {
        var job = ThumbnailImportJob.Create(titles, 250);
        await repository.AddJobAsync(job, ct);
        if (_wake.CurrentCount == 0) _wake.Release();
        return job.Id;
    }

    public Task<ThumbnailImportJob?> GetJobAsync(string id, CancellationToken ct) =>
        repository.GetJobAsync(id, ct);

    public async Task<IReadOnlyList<ThumbnailRendition?>> GetRenditionsAsync(ThumbnailImportJob job, CancellationToken ct)
    {
        var rows = new List<ThumbnailRendition?>();
        foreach (var title in job.Titles)
            rows.Add(await repository.GetRenditionAsync(title, job.Width, ct));
        return rows;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingAsync(stoppingToken);
                await _wake.WaitAsync(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogError(error, "Thumbnail import poll failed");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    public async Task ProcessPendingAsync(CancellationToken ct)
    {
        foreach (var job in await repository.ListIncompleteJobsAsync(ct))
            await RunJobAsync(job, ct);
    }

    private async Task RunJobAsync(ThumbnailImportJob job, CancellationToken ct)
    {
        await repository.SaveJobAsync(job with { Status = "running" }, ct);
        var failures = 0;
        foreach (var title in job.Titles)
        {
            ct.ThrowIfCancellationRequested();
            var identity = ThumbnailIdentity.For(title, job.Width);
            var row = await repository.GetRenditionAsync(title, job.Width, ct) ??
                new ThumbnailRendition(identity.RowKey, title, job.Width, identity.BlobName, "pending");
            try
            {
                await repository.EnsureAssetAsync(title, ct);
                if (await copier.IsCurrentAsync(row, ct)) continue;
                var recovered = await copier.RecoverAsync(row, ct);
                if (recovered is not null)
                {
                    await repository.SaveRenditionAsync(row with
                    {
                        Status = "complete", Bytes = recovered.Bytes, Sha256 = recovered.Sha256,
                        Error = null
                    }, ct);
                    continue;
                }
                await repository.SaveRenditionAsync(row with { Status = "running", Error = null }, ct);
                var info = (await metadata.ResolveAsync([title], job.Width, ct))[title];
                if (info.ThumbnailUrl is null)
                    throw new InvalidOperationException(info.Error ?? "Wikimedia has no thumbnail for this file.");
                var copied = await copier.CopyAsync(info.ThumbnailUrl, row, ct);
                await repository.SaveRenditionAsync(row with
                {
                    Status = "complete", Bytes = copied.Bytes, Sha256 = copied.Sha256,
                    SourceUrl = info.ThumbnailUrl, Error = null, Stale = false
                }, ct);
            }
            catch (WikimediaThrottledException error)
            {
                await repository.SaveRenditionAsync(row with { Status = "pending", Error = error.Message }, ct);
                logger.LogWarning("Wikimedia throttled thumbnail import {JobId}; retry in {Delay}", job.Id, error.Delay);
                await Task.Delay(error.Delay < TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : error.Delay, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                failures++;
                await repository.SaveRenditionAsync(row with { Status = "failed", Error = error.Message }, ct);
                logger.LogWarning(error, "Thumbnail import failed for {Title}", title);
            }
        }
        await repository.SaveJobAsync(job with { Status = failures == 0 ? "complete" : "complete-with-errors" }, ct);
    }
}
