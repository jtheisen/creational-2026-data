using Creational.Dataflow.Execution;
using Microsoft.Extensions.Hosting;

namespace Creational.Dataflow.Images;

public sealed class SqlResizeBatchDispatcher(SqlImageWorkRepository repository, SqlImageBlobStore blobs,
    ContainerAppsJobLauncher launcher, ILogger<SqlResizeBatchDispatcher> logger) : BackgroundService
{
    private const string Recipe = "webp-lanczos-q82-v1";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var open = await repository.ListOpenResizeBatchesAsync(stoppingToken);
                foreach (var batch in open) await ReconcileAsync(batch, stoppingToken);
                var setting = await repository.GetSettingAsync(ImageWorkKind.Resize, stoppingToken);
                if (open.Count == 0 && setting.Enabled)
                {
                    await repository.RecoverOrphanedResizeLeasesAsync(stoppingToken);
                    await repository.ActivateDueAsync(ImageWorkKind.Resize, stoppingToken);
                    await repository.FillPendingQueueAsync(ImageWorkKind.Resize, 10, stoppingToken);
                    await LaunchAsync(stoppingToken);
                }
                await Task.Delay(TimeSpan.FromSeconds(setting.Enabled || open.Count > 0 ? 15 : 30), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogError(error, "SQL resize batch cycle failed");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    private async Task LaunchAsync(CancellationToken ct)
    {
        var claims = await repository.ClaimAsync(ImageWorkKind.Resize, 20, TimeSpan.FromHours(4), ct);
        if (claims.Count == 0) return;
        var batchId = claims[0].LeaseId;
        string manifest;
        try
        {
            manifest = await blobs.WriteResizeManifestAsync(batchId, claims, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            foreach (var claim in claims)
                await repository.FailAsync(claim, error.Message, TimeSpan.FromMinutes(5), false, ct);
            throw;
        }
        var resultPath = $"sql-resize/results/{batchId:N}.json";
        await repository.CreateResizeBatchAsync(batchId, manifest, resultPath, ct);
        try
        {
            var execution = await launcher.StartSqlResizeAsync(manifest, ct);
            await repository.SetResizeBatchStatusAsync(batchId, 1, execution, null, ct);
            logger.LogInformation("Started SQL resize batch {BatchId} with {Count} images as {Execution}",
                batchId, claims.Count, execution);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await repository.SetResizeBatchStatusAsync(batchId, 2, null, error.Message, ct);
            logger.LogError(error, "Resize batch {BatchId} launch is uncertain; it will not be relaunched automatically",
                batchId);
        }
    }

    private async Task ReconcileAsync(ImageResizeBatch batch, CancellationToken ct)
    {
        var result = await blobs.ReadResizeResultAsync(batch.BatchId, ct);
        if (result is not null)
        {
            await ApplyResultAsync(batch, result, ct);
            return;
        }
        if (batch.ExecutionId is null)
        {
            if (batch.Status == 0 && DateTimeOffset.UtcNow - batch.CreatedAt > TimeSpan.FromMinutes(5))
                await repository.SetResizeBatchStatusAsync(batch.BatchId, 2, null,
                    "No execution ID was recorded; inspect Container Apps before releasing claims", ct);
            return;
        }
        var status = await launcher.GetStatusAsync(batch.ExecutionId, ct);
        if (status.State is ComputeExecutionState.Running or ComputeExecutionState.Unknown) return;
        if (status.State == ComputeExecutionState.Succeeded &&
            DateTimeOffset.UtcNow - batch.CreatedAt < TimeSpan.FromMinutes(2)) return;
        var claims = await repository.GetResizeBatchClaimsAsync(batch.BatchId, ct);
        var error = status.State == ComputeExecutionState.Succeeded
            ? "Worker succeeded without a result marker" : status.Message ?? "Resize worker failed";
        foreach (var claim in claims)
            await repository.FailAsync(claim, error, TimeSpan.FromMinutes(5), false, ct);
        await repository.SetResizeBatchStatusAsync(batch.BatchId, 4, batch.ExecutionId, error, ct);
    }

    private async Task ApplyResultAsync(ImageResizeBatch batch, SqlResizeResult result, CancellationToken ct)
    {
        var manifest = await blobs.ReadResizeManifestAsync(batch.ManifestPath, ct);
        if (!string.Equals(manifest.BatchId, result.BatchId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Resize result does not match its manifest batch ID.");
        var expected = manifest.Items.ToDictionary(x => x.ImageId);
        var actual = result.Items.ToDictionary(x => x.ImageId);
        var claims = await repository.GetResizeBatchClaimsAsync(batch.BatchId, ct);
        var failures = 0;
        foreach (var claim in claims)
        {
            var verified = new Dictionary<byte, int>();
            string? error = null;
            if (!expected.TryGetValue(claim.ImageId, out var source) ||
                !actual.TryGetValue(claim.ImageId, out var item) ||
                !string.Equals(source.SourceSha256, item.SourceSha256, StringComparison.OrdinalIgnoreCase))
                error = "Resize item missing or source checksum differs";
            else if (!await blobs.VerifySourceAsync(claim.ImageId, source.SourceBytes,
                         source.SourceSha256, ct))
                error = "Source Blob changed during resize";
            else
            {
                foreach (var width in new[] { 20, 40, 60, 120 })
                {
                    var shape = item.Shapes.SingleOrDefault(x => x.Width == width);
                    if (shape is null || shape.Status is not ("complete" or "current") ||
                        shape.Bytes is not > 0 || shape.Sha256 is not { Length: 64 } hash ||
                        !await blobs.VerifyDerivedAsync(claim.ImageId, width, shape.Bytes.Value,
                            hash, source.SourceSha256, Recipe, ct))
                    {
                        error ??= shape?.Error ?? $"Derived width {width} did not verify";
                        continue;
                    }
                    verified[width switch { 20 => 2, 40 => 3, 60 => 4, _ => 5 }] = shape.Bytes.Value;
                }
            }
            if (!await repository.CompleteResizeItemAsync(claim, verified, error, ct))
            {
                error = "Resize source generation or claim changed";
                await repository.FailAsync(claim, error, TimeSpan.FromMinutes(5), false, ct);
            }
            if (error is not null) failures++;
        }
        await repository.SetResizeBatchStatusAsync(batch.BatchId, failures == 0 ? (byte)3 : (byte)4,
            batch.ExecutionId, failures == 0 ? null : $"{failures} images require retry", ct);
        logger.LogInformation("Reconciled resize batch {BatchId}: {Count} images, {Failed} failed",
            batch.BatchId, claims.Count, failures);
    }
}
