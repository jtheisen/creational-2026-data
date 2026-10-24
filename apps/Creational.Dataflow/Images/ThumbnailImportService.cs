using Azure;
using Microsoft.Extensions.Hosting;

namespace Creational.Dataflow.Images;

public sealed class ThumbnailImportService(
    IThumbnailImportRepository repository,
    IThumbnailBlobCopier copier,
    WikimediaImageInfo metadata,
    ILogger<ThumbnailImportService> logger,
    IImageCatalog? catalog = null,
    IThumbnailBatchManifestStore? manifests = null,
    IImageInfoStore? infoStore = null,
    IResizeSweepScheduler? resizeScheduler = null) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0, 1);
    private ThumbnailBatchActivity? _activity;

    public ThumbnailBatchActivity? GetBatchActivity(string batchId)
    {
        var activity = Volatile.Read(ref _activity);
        return activity?.BatchId == batchId ? activity : null;
    }

    public async Task<string> CreateJobAsync(IReadOnlyList<string> titles, CancellationToken ct)
    {
        var job = ThumbnailImportJob.Create(titles, 250);
        await repository.AddJobAsync(job, ct);
        if (_wake.CurrentCount == 0) _wake.Release();
        return job.Id;
    }

    public Task<ThumbnailImportJob?> GetJobAsync(string id, CancellationToken ct) =>
        repository.GetJobAsync(id, ct);

    public async Task<string> CreateBatchAsync(ImageSelection selection, int offset, int count, CancellationToken ct)
    {
        var batch = ThumbnailImportBatch.Create(selection, offset, count);
        if (manifests is not null)
        {
            if (catalog is null) throw new InvalidOperationException("Image catalog is unavailable.");
            var page = await catalog.QueryAsync(selection, offset, count, ct);
            if (page.Items.Count == 0) throw new InvalidOperationException("No images remain in the selected range.");
            var path = await manifests.SaveAsync(new ThumbnailBatchManifest(batch.Id, page.Items.ToArray()), ct);
            batch = batch with { ManifestPath = path, RequestedCount = page.Items.Count };
        }
        await repository.AddBatchAsync(batch, ct);
        if (_wake.CurrentCount == 0) _wake.Release();
        return batch.Id;
    }

    public Task<ThumbnailImportBatch?> GetBatchAsync(string id, CancellationToken ct) =>
        repository.GetBatchAsync(id, ct);

    public Task<IReadOnlyList<ThumbnailImportBatch>> ListBatchesAsync(int limit, CancellationToken ct) =>
        repository.ListBatchesAsync(limit, ct);

    public Task<IReadOnlyList<ThumbnailImportBatch>> ListRunBatchesAsync(string id, CancellationToken ct) =>
        repository.ListRunBatchesAsync(id, ct);

    public Task<ThumbnailImportRun?> GetRunAsync(string id, CancellationToken ct) =>
        repository.GetRunAsync(id, ct);

    public Task<IReadOnlyList<ThumbnailImportRun>> ListRunsAsync(int limit, CancellationToken ct) =>
        repository.ListRunsAsync(limit, ct);

    public async Task<string> CreateRunAsync(ImageSelection selection, CancellationToken ct)
    {
        if (catalog is null || manifests is null)
            throw new InvalidOperationException("Image catalog and manifest storage are required for import runs.");
        var run = ThumbnailImportRun.Create(selection);
        var chunkIndex = 0;
        var total = await catalog.SnapshotAsync(selection, ThumbnailImportRun.ImagesPerBatch,
            async (chunk, token) =>
            {
                var id = run.BatchId(chunkIndex);
                await manifests.SaveAsync(new ThumbnailBatchManifest(id, chunk.ToArray()), token);
                chunkIndex++;
            }, ct);
        if (total == 0) throw new InvalidOperationException("No images were found in this selection.");
        run = run with { TotalImages = total };
        if (chunkIndex != run.BatchCount)
            throw new InvalidDataException("Image catalog snapshot produced an unexpected number of chunks.");
        await repository.AddRunAsync(run, ct);
        if (_wake.CurrentCount == 0) _wake.Release();
        return run.Id;
    }

    public Task<IReadOnlyList<ThumbnailImportJob>> ListJobsAsync(int limit, CancellationToken ct) =>
        repository.ListJobsAsync(limit, ct);

    public Task<IReadOnlyList<ThumbnailImportJob>> ListStandaloneJobsAsync(int limit, CancellationToken ct) =>
        repository.ListStandaloneJobsAsync(limit, ct);

    public Task<IReadOnlyList<ThumbnailImportJob>> ListBatchJobsAsync(string id, CancellationToken ct) =>
        repository.ListBatchJobsAsync(id, ct);

    public Task<IReadOnlyList<ThumbnailBatchItem>> ListBatchItemsAsync(string id, CancellationToken ct) =>
        repository.ListBatchItemsAsync(id, ct);

    public async Task ProcessJobAsync(string id, CancellationToken ct)
    {
        var job = await repository.GetJobAsync(id, ct) ?? throw new ArgumentException("Image job was not found.", nameof(id));
        if (job.Status is not ("complete" or "complete-with-errors"))
            await RunJobAsync(job, ct);
    }

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
        await ReconcileRunsAsync(ct);
        foreach (var job in (await repository.ListIncompleteJobsAsync(ct)).Where(x => x.BatchId is null))
            await RunJobAsync(job, ct);
        foreach (var batch in await repository.ListActiveBatchesAsync(ct))
            await RunBatchAsync(batch, ct);
        await ReconcileRunsAsync(ct);
    }

    private async Task ReconcileRunsAsync(CancellationToken ct)
    {
        foreach (var run in await repository.ListActiveRunsAsync(ct))
            await AdvanceRunAsync(run, ct);
    }

    private async Task AdvanceRunAsync(ThumbnailImportRun run, CancellationToken ct)
    {
        while (run.NextBatchIndex < run.BatchCount)
        {
            ct.ThrowIfCancellationRequested();
            var index = run.NextBatchIndex;
            var id = run.BatchId(index);
            var batch = await repository.GetBatchAsync(id, ct);
            if (batch is null)
            {
                var start = run.BatchStartOffset(index);
                var proposed = ThumbnailImportBatch.Create(run.Selection, start, run.BatchImageCount(index)) with
                {
                    Id = id, ManifestPath = run.ManifestPath(index), ParentRunId = run.Id, Width = run.Width
                };
                batch = await repository.AddBatchIfMissingAsync(proposed, ct);
            }
            if (batch.ParentRunId != run.Id || batch.ManifestPath != run.ManifestPath(index) ||
                batch.StartOffset != run.BatchStartOffset(index) ||
                batch.RequestedCount != run.BatchImageCount(index))
                throw new InvalidDataException("Image run child batch does not match its snapshot.");
            if (batch.Status is not ("complete" or "complete-with-errors"))
            {
                if (run.Status == "pending") await repository.SaveRunAsync(run with { Status = "running" }, ct);
                return;
            }
            run = run with
            {
                NextBatchIndex = index + 1,
                CopiedImages = checked(run.CopiedImages + batch.CopiedImages),
                SkippedImages = checked(run.SkippedImages + batch.SkippedImages),
                FailedImages = checked(run.FailedImages + batch.FailedImages),
                Status = index + 1 == run.BatchCount
                    ? run.FailedImages + batch.FailedImages == 0 ? "complete" : "complete-with-errors"
                    : "running",
                Error = null
            };
            await repository.SaveRunAsync(run, ct);
        }
    }

    private async Task RunBatchAsync(ThumbnailImportBatch batch, CancellationToken ct)
    {
        if (batch.ManifestPath is not null)
        {
            await RunUnifiedBatchAsync(batch, ct);
            return;
        }
        if (catalog is null) throw new InvalidOperationException("Image catalog is unavailable.");
        try
        {
            if (batch.Status == "pending")
            {
                batch = batch with { Status = "running", Error = null };
                await repository.SaveBatchAsync(batch, ct);
            }
            while (batch.NextOffset < batch.EndOffset)
            {
                ct.ThrowIfCancellationRequested();
                var id = batch.ChildId(batch.NextOffset);
                var job = await repository.GetJobAsync(id, ct);
                if (job is null)
                {
                    var page = await catalog.QueryAsync(batch.Selection, batch.NextOffset,
                        batch.EndOffset - batch.NextOffset, ct);
                    if (page.Items.Count == 0) break;
                    var offset = batch.NextOffset;
                    foreach (var chunk in page.Items.Chunk(24))
                    {
                        var child = ThumbnailImportJob.Create(chunk.Select(x => x.FileTitle).ToArray(), 250) with
                        {
                            Id = batch.ChildId(offset), BatchId = batch.Id, StartOffset = offset
                        };
                        var recorded = await repository.AddJobIfMissingAsync(child, ct);
                        if (offset == batch.NextOffset) job = recorded;
                        offset += chunk.Length;
                    }
                }
                if (job is null) throw new InvalidOperationException("Batch planning produced no child job.");
                if (job.Status is not ("complete" or "complete-with-errors"))
                {
                    await RunJobAsync(job, ct);
                    job = await repository.GetJobAsync(id, ct) ??
                        throw new InvalidOperationException("Child job disappeared during processing.");
                }
                if (job.Status is not ("complete" or "complete-with-errors")) return;
                batch = batch with
                {
                    NextOffset = batch.NextOffset + job.Titles.Length,
                    FailedJobs = batch.FailedJobs + (job.Status == "complete-with-errors" ? 1 : 0),
                    Status = "running", Error = null
                };
                await repository.SaveBatchAsync(batch, ct);
            }
            await repository.SaveBatchAsync(batch with
            {
                Status = batch.FailedJobs == 0 ? "complete" : "complete-with-errors", Error = null
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            await repository.SaveBatchAsync(batch with { Error = error.Message }, ct);
            throw;
        }
    }

    private async Task RunUnifiedBatchAsync(ThumbnailImportBatch batch, CancellationToken ct)
    {
        if (manifests is null || infoStore is null)
            throw new InvalidOperationException("Unified image import services are unavailable.");
        var manifest = await manifests.ReadAsync(batch.ManifestPath!, ct);
        if (manifest.BatchId != batch.Id || manifest.Images.Length != batch.RequestedCount)
            throw new InvalidDataException("Image batch manifest does not match its Table record.");
        if (batch.Status == "pending")
        {
            batch = batch with { Status = "running", Error = null };
            await repository.SaveBatchAsync(batch, ct);
        }
        try { await RunUnifiedBatchCoreAsync(batch, manifest, infoStore, ct); }
        finally { Volatile.Write(ref _activity, null); }
    }

    private async Task RunUnifiedBatchCoreAsync(ThumbnailImportBatch batch,
        ThumbnailBatchManifest manifest, IImageInfoStore infoStore, CancellationToken ct)
    {
        while (batch.NextOffset < batch.EndOffset)
        {
            ct.ThrowIfCancellationRequested();
            var first = batch.NextOffset - batch.StartOffset;
            var window = manifest.Images.Skip(first).Take(30).ToArray();
            Volatile.Write(ref _activity, new ThumbnailBatchActivity(batch.Id,
                ThumbnailBatchWorkStage.Metadata, batch.NextOffset, window.Length));
            IReadOnlyDictionary<string, StoredImageInfo> metadata;
            try { metadata = await infoStore.EnsureAsync(window, ct); }
            catch (WikimediaThrottledException error)
            {
                logger.LogWarning("Wikimedia throttled image metadata for batch {BatchId}; retry in {Delay}",
                    batch.Id, error.Delay);
                await Task.Delay(error.Delay < TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : error.Delay, ct);
                return;
            }
            foreach (var candidate in window)
            {
                var offset = batch.NextOffset;
                Volatile.Write(ref _activity, new ThumbnailBatchActivity(batch.Id,
                    ThumbnailBatchWorkStage.Copy, offset, 1));
                var existing = await repository.GetBatchItemAsync(batch.Id, offset, ct);
                ThumbnailBatchItem item;
                if (existing is { Status: "copied" or "skipped" or "failed" })
                    item = existing;
                else
                {
                    try
                    {
                        var info = metadata[candidate.FileTitle];
                        if (info.Status != "complete")
                            throw new InvalidOperationException(info.Error ?? "Image information is unavailable.");
                        var identity = ThumbnailIdentity.For(candidate.FileTitle, batch.Width);
                        var rendition = await repository.GetRenditionAsync(candidate.FileTitle, batch.Width, ct) ??
                            new ThumbnailRendition(identity.RowKey, candidate.FileTitle, batch.Width, identity.BlobName, "pending");
                        if (await copier.IsCurrentAsync(rendition, ct))
                            item = new(batch.Id, offset, candidate.FileTitle, "skipped");
                        else
                        {
                            var recovered = await copier.RecoverAsync(rendition, ct);
                            if (recovered is not null)
                            {
                                await repository.SaveRenditionAsync(rendition with
                                {
                                    Status = "complete", Bytes = recovered.Bytes,
                                    Sha256 = recovered.Sha256, Error = null, Stale = false
                                }, ct);
                                item = new(batch.Id, offset, candidate.FileTitle, "skipped");
                            }
                            else
                            {
                                await repository.SaveRenditionAsync(rendition with { Status = "running", Error = null }, ct);
                                var source = WikimediaImageInfo.ThumbnailRedirect(info.SourceWiki,
                                    info.ResolvedTitle ?? info.Title, batch.Width);
                                var copied = await copier.CopyAsync(source, rendition, ct);
                                await repository.SaveRenditionAsync(rendition with
                                {
                                    Status = "complete", Bytes = copied.Bytes, Sha256 = copied.Sha256,
                                    SourceUrl = null, Error = null, Stale = false
                                }, ct);
                                item = new(batch.Id, offset, candidate.FileTitle, "copied", copied.Bytes);
                            }
                        }
                    }
                    catch (WikimediaThrottledException error)
                    {
                        logger.LogWarning("Wikimedia throttled image copy for batch {BatchId}; retry in {Delay}",
                            batch.Id, error.Delay);
                        await Task.Delay(error.Delay < TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : error.Delay, ct);
                        return;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (RequestFailedException) { throw; }
                    catch (HttpRequestException error) when (error.StatusCode is null or >= System.Net.HttpStatusCode.InternalServerError)
                    { throw; }
                    catch (Exception error)
                    {
                        logger.LogWarning(error, "Image import failed for {Title}", candidate.FileTitle);
                        item = new(batch.Id, offset, candidate.FileTitle, "failed", Error: error.Message);
                    }
                    await repository.SaveBatchItemAsync(item, ct);
                }
                batch = batch with
                {
                    NextOffset = offset + 1,
                    CopiedImages = batch.CopiedImages + (item.Status == "copied" ? 1 : 0),
                    SkippedImages = batch.SkippedImages + (item.Status == "skipped" ? 1 : 0),
                    FailedImages = batch.FailedImages + (item.Status == "failed" ? 1 : 0),
                    Error = null
                };
                await repository.SaveBatchAsync(batch, ct);
            }
        }
        if (batch.CopiedImages > 0 && resizeScheduler is not null)
            await resizeScheduler.RequestAsync(ct);
        await repository.SaveBatchAsync(batch with
        {
            Status = batch.FailedImages == 0 ? "complete" : "complete-with-errors", Error = null
        }, ct);
    }

    private async Task RunJobAsync(ThumbnailImportJob job, CancellationToken ct)
    {
        await repository.SaveJobAsync(job with { Status = "running" }, ct);
        var failures = 0;
        var copiedAny = false;
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
                copiedAny = true;
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
        if (copiedAny && resizeScheduler is not null)
            await resizeScheduler.RequestAsync(ct);
        await repository.SaveJobAsync(job with { Status = failures == 0 ? "complete" : "complete-with-errors" }, ct);
    }
}
