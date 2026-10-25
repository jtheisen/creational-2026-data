using Azure;
using Azure.Data.Tables;
using Microsoft.Data.SqlClient;

namespace Creational.Dataflow.Images;

public sealed partial class SqlImageAdopter
{
    private readonly record struct AdoptionOutcome(int Copied, int Quarantined);

    public async Task<ImageAdoptionTotals> RunAsync(int limitPerPartition, bool apply, CancellationToken ct)
    {
        if (limitPerPartition < 1) throw new ArgumentOutOfRangeException(nameof(limitPerPartition));
        if (apply) await blobs.EnsureContainerAsync(ct);
        await using var connection = await factory.OpenAsync(ct);
        var assetRows = new List<TableEntity>();
        await foreach (var row in table.QueryAsync<TableEntity>(filter: "PartitionKey eq '__thumbnail_assets'",
            cancellationToken: ct)) assetRows.Add(row);
        var matches = await BuildMatchesAsync(connection, assetRows, apply, ct);
        var assetCheckpoint = apply ? await GetCheckpointAsync(connection, "__thumbnail_assets", ct) : "";
        var renditionCheckpoint = apply ? await GetCheckpointAsync(connection, "__thumbnail_renditions", ct) : "";
        var assetsMatched = assetRows.Count(row => row.GetString("Title") is { } title && matches.ContainsKey(title));
        var copiedMetadata = 0;
        var copiedRenditions = 0;
        var quarantined = 0;
        var processedAssets = 0;
        foreach (var chunk in assetRows.Where(x => string.CompareOrdinal(x.RowKey, assetCheckpoint) > 0)
                     .Take(limitPerPartition).Chunk(100))
        {
            var result = await ProcessBatchAsync(chunk,
                (row, token) => ProcessAssetAsync(row, matches, apply, token), ct);
            processedAssets += chunk.Length;
            copiedMetadata += result.Copied;
            quarantined += result.Quarantined;
            if (apply)
            {
                await SaveCheckpointAsync(connection, "__thumbnail_assets", chunk[^1].RowKey, chunk.Length, ct);
                Console.Error.WriteLine($"Image metadata adoption: {processedAssets} processed, {copiedMetadata} copied, {quarantined} issues");
            }
        }

        var renditionRows = new List<TableEntity>(100);
        var processedRenditions = 0;
        async Task FlushRenditionsAsync()
        {
            if (renditionRows.Count == 0) return;
            var result = await ProcessBatchAsync(renditionRows,
                (row, token) => ProcessRenditionAsync(row, matches, apply, token), ct);
            processedRenditions += renditionRows.Count;
            copiedRenditions += result.Copied;
            quarantined += result.Quarantined;
            if (apply)
            {
                await SaveCheckpointAsync(connection, "__thumbnail_renditions", renditionRows[^1].RowKey,
                    renditionRows.Count, ct);
                Console.Error.WriteLine($"Image rendition adoption: {processedRenditions} processed, {copiedRenditions} copied, {quarantined} issues");
            }
            renditionRows.Clear();
        }
        await foreach (var row in table.QueryAsync<TableEntity>(filter: "PartitionKey eq '__thumbnail_renditions'",
            cancellationToken: ct))
        {
            if (string.CompareOrdinal(row.RowKey, renditionCheckpoint) <= 0) continue;
            if (processedRenditions + renditionRows.Count >= limitPerPartition) break;
            renditionRows.Add(row);
            if (renditionRows.Count == 100) await FlushRenditionsAsync();
        }
        await FlushRenditionsAsync();
        return new(assetRows.Count, assetsMatched, copiedMetadata, processedRenditions,
            copiedRenditions, quarantined);
    }

    private async Task<AdoptionOutcome> ProcessAssetAsync(TableEntity row,
        IReadOnlyDictionary<string, int> matches, bool apply, CancellationToken ct)
    {
        var title = row.GetString("Title");
        if (title is null || !matches.TryGetValue(title, out var imageId))
        {
            if (apply) await NoteIssueAsync(row, "unmatched-title", title, ct);
            return new(0, 1);
        }
        var status = row.GetString("MetadataStatus");
        var path = row.GetString("MetadataRawPath");
        if (string.IsNullOrWhiteSpace(status) || string.IsNullOrWhiteSpace(path)) return default;
        if (!apply) return default;
        try
        {
            var properties = (await blobs.LegacyClient(path).GetPropertiesAsync(cancellationToken: ct)).Value;
            var existing = await blobs.CopyLegacyAsync(path, SqlImageBlobStore.MetadataPath(imageId),
                properties.ContentLength, null, ct);
            await using var connection = await factory.OpenAsync(ct);
            await SaveMetadataAsync(connection, imageId, status, row, ct);
            await ClearIssueAsync(connection, row.PartitionKey, row.RowKey, ct);
            return new(existing ? 0 : 1, 0);
        }
        catch (RequestFailedException error) when (error.Status == 404)
        {
            await NoteIssueAsync(row, "metadata-blob-missing", title, ct);
            return new(0, 1);
        }
        catch (InvalidDataException)
        {
            await NoteIssueAsync(row, "metadata-invalid", title, ct);
            return new(0, 1);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await NoteIssueAsync(row, "metadata-copy-failed", title, ct);
            throw new InvalidOperationException($"Metadata adoption stopped at {row.RowKey}: {error.Message}", error);
        }
    }

    private async Task<AdoptionOutcome> ProcessRenditionAsync(TableEntity row,
        IReadOnlyDictionary<string, int> matches, bool apply, CancellationToken ct)
    {
        if (row.GetString("Status") != "complete") return default;
        var title = row.GetString("Title");
        var width = row.GetInt32("Width");
        var bytes = row.GetInt64("Bytes");
        var hash = row.GetString("Sha256");
        var path = row.GetString("BlobName");
        if (title is null || !matches.TryGetValue(title, out var imageId) ||
            width is null || Shape(width.Value) is null || bytes is null or < 1 or > int.MaxValue ||
            string.IsNullOrWhiteSpace(hash) || string.IsNullOrWhiteSpace(path))
        {
            if (apply) await NoteIssueAsync(row, "invalid-rendition", title, ct);
            return new(0, 1);
        }
        if (!apply) return default;
        try
        {
            var existing = await blobs.CopyLegacyAsync(path,
                SqlImageBlobStore.RenditionPath(imageId, width.Value), bytes.Value, hash, ct);
            await using var connection = await factory.OpenAsync(ct);
            await SaveRenditionAsync(connection, imageId, Shape(width.Value)!.Value,
                (int)bytes.Value, row.GetBoolean("Stale") ?? false, ct);
            await ClearIssueAsync(connection, row.PartitionKey, row.RowKey, ct);
            return new(existing ? 0 : 1, 0);
        }
        catch (RequestFailedException error) when (error.Status == 404)
        {
            await NoteIssueAsync(row, "rendition-blob-missing", title, ct);
            return new(0, 1);
        }
        catch (InvalidDataException)
        {
            await NoteIssueAsync(row, "rendition-invalid", title, ct);
            return new(0, 1);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await NoteIssueAsync(row, "rendition-copy-failed", title, ct);
            throw new InvalidOperationException($"Rendition adoption stopped at {row.RowKey}: {error.Message}", error);
        }
    }

    private async Task NoteIssueAsync(TableEntity row, string reason, string? title, CancellationToken ct)
    {
        await using var connection = await factory.OpenAsync(ct);
        await IssueAsync(connection, row.PartitionKey, row.RowKey, reason, title, ct);
    }

    private static async Task<AdoptionOutcome> ProcessBatchAsync(IReadOnlyList<TableEntity> rows,
        Func<TableEntity, CancellationToken, Task<AdoptionOutcome>> process, CancellationToken ct)
    {
        var outcomes = new AdoptionOutcome[rows.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, rows.Count), new ParallelOptions
        { MaxDegreeOfParallelism = 10, CancellationToken = ct },
            async (index, token) => outcomes[index] = await process(rows[index], token));
        return new(outcomes.Sum(x => x.Copied), outcomes.Sum(x => x.Quarantined));
    }
}
