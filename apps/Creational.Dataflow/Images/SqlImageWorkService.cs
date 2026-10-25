using Microsoft.Extensions.Hosting;

namespace Creational.Dataflow.Images;

public sealed class SqlImageWorkService(SqlImageWorkRepository repository, WikimediaImageInfo api,
    SqlImageBlobStore blobs, ThumbnailBlobCopier copier, WikimediaRequestPacer pacer,
    ILogger<SqlImageWorkService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var metadataSetting = await repository.GetSettingAsync(ImageWorkKind.Metadata, stoppingToken);
                var copySetting = await repository.GetSettingAsync(ImageWorkKind.Copy, stoppingToken);
                pacer.SetMinimumInterval(TimeSpan.FromMilliseconds(Math.Max(
                    metadataSetting.Enabled ? metadataSetting.PacingMilliseconds : 1000,
                    copySetting.Enabled ? copySetting.PacingMilliseconds : 1000)));
                if (!metadataSetting.Enabled && !copySetting.Enabled)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    continue;
                }
                if (metadataSetting.Enabled)
                {
                    await repository.RecoverExpiredAsync(ImageWorkKind.Metadata, stoppingToken);
                    await repository.ActivateDueAsync(ImageWorkKind.Metadata, stoppingToken);
                    await repository.FillMetadataQueueAsync(10, stoppingToken);
                }
                if (copySetting.Enabled)
                {
                    await repository.RecoverExpiredAsync(ImageWorkKind.Copy, stoppingToken);
                    await repository.ActivateDueAsync(ImageWorkKind.Copy, stoppingToken);
                    await repository.FillPendingQueueAsync(ImageWorkKind.Copy, 10, stoppingToken);
                }
                var metadata = metadataSetting.Enabled ? await repository.ClaimAsync(ImageWorkKind.Metadata, 10,
                    TimeSpan.FromMinutes(10), stoppingToken) : [];
                if (metadata.Count > 0) await ProcessMetadataAsync(metadata, stoppingToken);
                var copies = copySetting.Enabled ? await repository.ClaimAsync(ImageWorkKind.Copy, 5,
                    TimeSpan.FromMinutes(15), stoppingToken) : [];
                if (copies.Count > 0) await Task.WhenAll(copies.Select(x => ProcessCopyAsync(x, stoppingToken)));
                if (metadata.Count == 0 && copies.Count == 0)
                    await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogError(error, "SQL image work cycle failed");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    private async Task ProcessMetadataAsync(IReadOnlyList<ImageWorkClaim> claims, CancellationToken ct)
    {
        foreach (var group in claims.GroupBy(x => x.SourceWiki, StringComparer.Ordinal))
        {
            IReadOnlyDictionary<string, ImageInfoSnapshot> snapshots;
            try
            {
                snapshots = await api.FetchSnapshotsAsync(group.Select(x => x.FileTitle).ToArray(), group.Key, ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                foreach (var claim in group)
                    await FailAsync(claim, error, ct);
                continue;
            }
            foreach (var claim in group)
            {
                try
                {
                    var snapshot = snapshots[claim.FileTitle];
                    await blobs.SaveMetadataAsync(claim.ImageId, snapshot, ct,
                        token => repository.IsLeaseCurrentAsync(claim, token));
                    var category = SqlImageAdopter.License(snapshot.Metadata.License);
                    if (!await repository.CompleteMetadataAsync(claim,
                            (byte)(snapshot.Status == "complete" ? 1 : 2), category, ct))
                        logger.LogWarning("Metadata lease expired before completion for image {ImageId}", claim.ImageId);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    await FailAsync(claim, error, ct);
                }
            }
        }
    }

    private async Task ProcessCopyAsync(ImageWorkClaim claim, CancellationToken ct)
    {
        try
        {
            var snapshot = await blobs.LoadMetadataAsync(claim.ImageId, ct);
            if (snapshot.Status != "complete")
            {
                await repository.FailAsync(claim, "Imageinfo has no usable image", TimeSpan.Zero, true, ct);
                return;
            }
            var path = SqlImageBlobStore.RenditionPath(claim.ImageId, 250);
            var rendition = new ThumbnailRendition($"sql-{claim.ImageId}-250", claim.FileTitle, 250,
                path, "pending");
            var recovered = await repository.IsSourceStaleAsync(claim.ImageId, ct)
                ? null : await copier.RecoverAsync(rendition, ct);
            var result = recovered ?? await copier.CopyAsync(
                WikimediaImageInfo.ThumbnailRedirect(claim.SourceWiki, snapshot.Metadata.FileTitle, 250),
                rendition, ct, token => repository.IsLeaseCurrentAsync(claim, token));
            if (!await repository.CompleteRenditionAsync(claim, 1, checked((int)result.Bytes), ct))
                logger.LogWarning("Copy lease expired before completion for image {ImageId}", claim.ImageId);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await FailAsync(claim, error, ct);
        }
    }

    private async Task FailAsync(ImageWorkClaim claim, Exception error, CancellationToken ct)
    {
        var delay = error is WikimediaThrottledException throttled ? throttled.Delay :
            TimeSpan.FromSeconds(Math.Min(3600, 30 * Math.Pow(2, Math.Min(claim.AttemptNumber, 7))));
        var blocked = error is InvalidDataException or ArgumentException;
        await repository.FailAsync(claim, error.Message, delay, blocked, ct);
        logger.LogWarning(error, "Image work failed for image {ImageId}, kind {Kind}, attempt {Attempt}",
            claim.ImageId, claim.Kind, claim.AttemptNumber);
    }
}
