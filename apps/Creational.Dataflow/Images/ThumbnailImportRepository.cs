using System.Text.Json;
using Azure;
using Azure.Data.Tables;

namespace Creational.Dataflow.Images;

public interface IThumbnailImportRepository
{
    Task AddJobAsync(ThumbnailImportJob job, CancellationToken ct);
    Task<ThumbnailImportJob> AddJobIfMissingAsync(ThumbnailImportJob job, CancellationToken ct);
    Task<ThumbnailImportJob?> GetJobAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<ThumbnailImportJob>> ListJobsAsync(int limit, CancellationToken ct);
    Task<IReadOnlyList<ThumbnailImportJob>> ListStandaloneJobsAsync(int limit, CancellationToken ct);
    Task<IReadOnlyList<ThumbnailImportJob>> ListBatchJobsAsync(string batchId, CancellationToken ct);
    Task<IReadOnlyList<ThumbnailImportJob>> ListIncompleteJobsAsync(CancellationToken ct);
    Task SaveJobAsync(ThumbnailImportJob job, CancellationToken ct);
    Task AddBatchAsync(ThumbnailImportBatch batch, CancellationToken ct);
    Task<ThumbnailImportBatch?> GetBatchAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<ThumbnailImportBatch>> ListBatchesAsync(int limit, CancellationToken ct);
    Task<IReadOnlyList<ThumbnailImportBatch>> ListActiveBatchesAsync(CancellationToken ct);
    Task SaveBatchAsync(ThumbnailImportBatch batch, CancellationToken ct);
    Task<ThumbnailBatchItem?> GetBatchItemAsync(string batchId, int offset, CancellationToken ct);
    Task<IReadOnlyList<ThumbnailBatchItem>> ListBatchItemsAsync(string batchId, CancellationToken ct);
    Task SaveBatchItemAsync(ThumbnailBatchItem item, CancellationToken ct);
    Task<ThumbnailRendition?> GetRenditionAsync(string title, int width, CancellationToken ct);
    Task SaveRenditionAsync(ThumbnailRendition rendition, CancellationToken ct);
    Task EnsureAssetAsync(string title, CancellationToken ct);
    Task<StoredImageInfo?> GetImageInfoAsync(string title, CancellationToken ct);
    Task SaveImageInfoAsync(StoredImageInfo info, CancellationToken ct);
}

public sealed class ThumbnailImportRepository(TableClient table) : IThumbnailImportRepository
{
    private const string Jobs = "__thumbnail_jobs";
    private const string Batches = "__thumbnail_batches";
    private const string Renditions = "__thumbnail_renditions";
    private const string Assets = "__thumbnail_assets";
    private const string BatchItems = "__thumbnail_batch_items";

    public async Task AddJobAsync(ThumbnailImportJob job, CancellationToken ct) =>
        await table.AddEntityAsync(JobEntity(job), ct);

    public async Task<ThumbnailImportJob> AddJobIfMissingAsync(ThumbnailImportJob job, CancellationToken ct)
    {
        try { await AddJobAsync(job, ct); return job; }
        catch (RequestFailedException e) when (e.Status == 409)
        {
            return await GetJobAsync(job.Id, ct) ?? throw new InvalidOperationException("Child job disappeared after a duplicate insert.");
        }
    }

    public async Task<ThumbnailImportJob?> GetJobAsync(string id, CancellationToken ct)
    {
        var response = await table.GetEntityIfExistsAsync<TableEntity>(Jobs, id, cancellationToken: ct);
        return response.HasValue ? ReadJob(response.Value!) : null;
    }

    public async Task<IReadOnlyList<ThumbnailImportJob>> ListIncompleteJobsAsync(CancellationToken ct)
    {
        var result = new List<ThumbnailImportJob>();
        // Use an OData filter; expression binding fails in the deployed App Service runtime.
        await foreach (var row in table.QueryAsync<TableEntity>(filter: "PartitionKey eq '__thumbnail_jobs'",
            cancellationToken: ct))
        {
            var job = ReadJob(row);
            if (job.Status is "pending" or "running") result.Add(job);
        }
        return result.OrderBy(x => x.CreatedAt).ToArray();
    }

    public async Task<IReadOnlyList<ThumbnailImportJob>> ListJobsAsync(int limit, CancellationToken ct)
    {
        var jobs = new List<ThumbnailImportJob>();
        await foreach (var row in table.QueryAsync<TableEntity>(filter: "PartitionKey eq '__thumbnail_jobs'",
            cancellationToken: ct)) jobs.Add(ReadJob(row));
        return jobs.OrderByDescending(x => x.CreatedAt).Take(limit).ToArray();
    }

    public async Task<IReadOnlyList<ThumbnailImportJob>> ListStandaloneJobsAsync(int limit, CancellationToken ct)
    {
        var jobs = await ListJobsAsync(int.MaxValue, ct);
        return jobs.Where(x => x.BatchId is null).Take(limit).ToArray();
    }

    public async Task<IReadOnlyList<ThumbnailImportJob>> ListBatchJobsAsync(string batchId, CancellationToken ct)
    {
        var jobs = new List<ThumbnailImportJob>();
        var id = batchId.Replace("'", "''", StringComparison.Ordinal);
        await foreach (var row in table.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '__thumbnail_jobs' and RowKey ge '{id}-' and RowKey lt '{id}.'",
            cancellationToken: ct))
            jobs.Add(ReadJob(row));
        return jobs.OrderBy(x => x.StartOffset).ToArray();
    }

    public async Task SaveJobAsync(ThumbnailImportJob job, CancellationToken ct) =>
        await table.UpsertEntityAsync(JobEntity(job), TableUpdateMode.Replace, ct);

    public async Task AddBatchAsync(ThumbnailImportBatch batch, CancellationToken ct) =>
        await table.AddEntityAsync(BatchEntity(batch), ct);

    public async Task<ThumbnailImportBatch?> GetBatchAsync(string id, CancellationToken ct)
    {
        var row = await table.GetEntityIfExistsAsync<TableEntity>(Batches, id, cancellationToken: ct);
        return row.HasValue ? ReadBatch(row.Value!) : null;
    }

    public async Task<IReadOnlyList<ThumbnailImportBatch>> ListBatchesAsync(int limit, CancellationToken ct)
    {
        var batches = new List<ThumbnailImportBatch>();
        await foreach (var row in table.QueryAsync<TableEntity>(filter: "PartitionKey eq '__thumbnail_batches'",
            cancellationToken: ct)) batches.Add(ReadBatch(row));
        return batches.OrderByDescending(x => x.CreatedAt).Take(limit).ToArray();
    }

    public async Task<IReadOnlyList<ThumbnailImportBatch>> ListActiveBatchesAsync(CancellationToken ct)
    {
        var batches = await ListBatchesAsync(int.MaxValue, ct);
        return batches.Where(x => x.Status is "pending" or "running")
            .OrderBy(x => x.CreatedAt).ToArray();
    }

    public async Task SaveBatchAsync(ThumbnailImportBatch batch, CancellationToken ct) =>
        await table.UpsertEntityAsync(BatchEntity(batch), TableUpdateMode.Replace, ct);

    public async Task<ThumbnailBatchItem?> GetBatchItemAsync(string batchId, int offset, CancellationToken ct)
    {
        var key = $"{batchId}-{offset:D8}";
        var response = await table.GetEntityIfExistsAsync<TableEntity>(BatchItems, key, cancellationToken: ct);
        return response.HasValue ? ReadBatchItem(response.Value!) : null;
    }

    public async Task<IReadOnlyList<ThumbnailBatchItem>> ListBatchItemsAsync(string batchId, CancellationToken ct)
    {
        var rows = new List<ThumbnailBatchItem>();
        var id = batchId.Replace("'", "''", StringComparison.Ordinal);
        await foreach (var row in table.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '__thumbnail_batch_items' and RowKey ge '{id}-' and RowKey lt '{id}.'",
            cancellationToken: ct)) rows.Add(ReadBatchItem(row));
        return rows.OrderBy(x => x.Offset).ToArray();
    }

    public async Task SaveBatchItemAsync(ThumbnailBatchItem item, CancellationToken ct)
    {
        var row = new TableEntity(BatchItems, item.RowKey)
        {
            ["BatchId"] = item.BatchId, ["Offset"] = item.Offset,
            ["Title"] = item.Title, ["Status"] = item.Status
        };
        if (item.Bytes is { } bytes) row["Bytes"] = bytes;
        if (item.Error is { } error) row["Error"] = error[..Math.Min(1024, error.Length)];
        await table.UpsertEntityAsync(row, TableUpdateMode.Replace, ct);
    }

    private static ThumbnailBatchItem ReadBatchItem(TableEntity row) => new(
        row.GetString("BatchId")!, row.GetInt32("Offset")!.Value,
        row.GetString("Title")!, row.GetString("Status")!, row.GetInt64("Bytes"), row.GetString("Error"));

    public async Task<ThumbnailRendition?> GetRenditionAsync(string title, int width, CancellationToken ct)
    {
        var identity = ThumbnailIdentity.For(title, width);
        var response = await table.GetEntityIfExistsAsync<TableEntity>(Renditions, identity.RowKey,
            cancellationToken: ct);
        if (!response.HasValue) return null;
        var row = response.Value!;
        return new(identity.RowKey, row.GetString("Title")!, width, identity.BlobName,
            row.GetString("Status")!, row.GetInt64("Bytes"), row.GetString("Sha256"),
            row.GetString("SourceUrl"), row.GetString("Error"), row.GetBoolean("Stale") ?? false);
    }

    public async Task SaveRenditionAsync(ThumbnailRendition rendition, CancellationToken ct)
    {
        var row = new TableEntity(Renditions, rendition.RowKey)
        {
            ["Title"] = rendition.Title, ["Width"] = rendition.Width,
            ["Status"] = rendition.Status, ["BlobName"] = rendition.BlobName,
            ["Stale"] = rendition.Stale
        };
        if (rendition.Bytes is { } bytes) row["Bytes"] = bytes;
        if (rendition.Sha256 is { } sha) row["Sha256"] = sha;
        if (rendition.SourceUrl is { } url) row["SourceUrl"] = url;
        if (rendition.Error is { } error) row["Error"] = error[..Math.Min(1024, error.Length)];
        await table.UpsertEntityAsync(row, TableUpdateMode.Replace, ct);
    }

    public async Task EnsureAssetAsync(string title, CancellationToken ct)
    {
        var key = ThumbnailIdentity.For(title, 250).RowKey[..64];
        try
        {
            await table.AddEntityAsync(new TableEntity(Assets, key)
            {
                ["Title"] = title, ["Curation"] = "unreviewed"
            }, ct);
        }
        catch (RequestFailedException e) when (e.Status == 409) { }
    }

    public async Task<StoredImageInfo?> GetImageInfoAsync(string title, CancellationToken ct)
    {
        var key = ThumbnailIdentity.For(title, 250).RowKey[..64];
        var response = await table.GetEntityIfExistsAsync<TableEntity>(Assets, key, cancellationToken: ct);
        if (!response.HasValue || string.IsNullOrEmpty(response.Value!.GetString("MetadataStatus"))) return null;
        var row = response.Value!;
        return new(title, row.GetString("SourceWiki")!, row.GetString("MetadataStatus")!,
            row.GetString("MetadataRawPath")!, row.GetDateTimeOffset("MetadataFetchedAt")!.Value,
            EmptyToNull(row.GetString("SourceSha1")),
            row.GetDateTimeOffset("SourceUploadedAt") is { } uploaded && uploaded != DateTimeOffset.UnixEpoch
                ? uploaded : null,
            row.GetInt64("OriginalBytes") is { } bytes && bytes >= 0 ? bytes : null,
            EmptyToNull(row.GetString("License")), EmptyToNull(row.GetString("LicenseUrl")),
            EmptyToNull(row.GetString("Attribution")), EmptyToNull(row.GetString("MetadataError")),
            row.GetBoolean("MetadataStale") ?? false, EmptyToNull(row.GetString("ResolvedTitle")));
    }

    public async Task SaveImageInfoAsync(StoredImageInfo info, CancellationToken ct)
    {
        await EnsureAssetAsync(info.Title, ct);
        var key = ThumbnailIdentity.For(info.Title, 250).RowKey[..64];
        var row = new TableEntity(Assets, key)
        {
            ["MetadataStatus"] = info.Status, ["SourceWiki"] = info.SourceWiki,
            ["MetadataRawPath"] = info.RawPath, ["MetadataFetchedAt"] = info.FetchedAt,
            ["MetadataStale"] = info.Stale,
            ["ResolvedTitle"] = info.ResolvedTitle ?? info.Title,
            ["SourceSha1"] = info.Sha1 ?? "",
            ["SourceUploadedAt"] = info.UploadedAt ?? DateTimeOffset.UnixEpoch,
            ["OriginalBytes"] = info.OriginalBytes ?? -1,
            ["License"] = info.License ?? "", ["LicenseUrl"] = info.LicenseUrl ?? "",
            ["Attribution"] = info.Attribution ?? "", ["MetadataError"] = info.Error ?? ""
        };
        if (info.Error is { Length: > 1024 } error) row["MetadataError"] = error[..1024];
        await table.UpsertEntityAsync(row, TableUpdateMode.Merge, ct);
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static TableEntity JobEntity(ThumbnailImportJob job)
    {
        var row = new TableEntity(Jobs, job.Id)
        {
            ["Width"] = job.Width, ["Titles"] = JsonSerializer.Serialize(job.Titles),
            ["Status"] = job.Status, ["CreatedAt"] = job.CreatedAt,
            ["Error"] = job.Error ?? ""
        };
        if (job.BatchId is not null) row["BatchId"] = job.BatchId;
        if (job.StartOffset is { } offset) row["StartOffset"] = offset;
        return row;
    }

    private static ThumbnailImportJob ReadJob(TableEntity row)
    {
        var batchId = row.GetString("BatchId");
        var startOffset = row.GetInt32("StartOffset");
        // An older deployed worker can replace a child row without the new fields during rollout.
        if (batchId is null && row.RowKey.Length == 41 && row.RowKey[32] == '-' &&
            Guid.TryParseExact(row.RowKey[..32], "N", out _) &&
            int.TryParse(row.RowKey[33..], out var parsedOffset))
        {
            batchId = row.RowKey[..32];
            startOffset = parsedOffset;
        }
        return new(row.RowKey, row.GetInt32("Width")!.Value,
            JsonSerializer.Deserialize<string[]>(row.GetString("Titles")!)!,
            row.GetString("Status")!, row.GetDateTimeOffset("CreatedAt")!.Value,
            string.IsNullOrEmpty(row.GetString("Error")) ? null : row.GetString("Error"))
        {
            BatchId = batchId, StartOffset = startOffset
        };
    }

    private static TableEntity BatchEntity(ThumbnailImportBatch batch) => new(Batches, batch.Id)
    {
        ["SelectionKind"] = batch.Selection.Kind.ToString(),
        ["SelectionValue"] = batch.Selection.Value,
        ["StartOffset"] = batch.StartOffset,
        ["RequestedCount"] = batch.RequestedCount,
        ["NextOffset"] = batch.NextOffset,
        ["Status"] = batch.Status,
        ["CreatedAt"] = batch.CreatedAt,
        ["FailedJobs"] = batch.FailedJobs,
        ["Width"] = batch.Width,
        ["CopiedImages"] = batch.CopiedImages,
        ["SkippedImages"] = batch.SkippedImages,
        ["FailedImages"] = batch.FailedImages,
        ["ManifestPath"] = batch.ManifestPath ?? "",
        ["Error"] = batch.Error ?? ""
    };

    private static ThumbnailImportBatch ReadBatch(TableEntity row) => new(row.RowKey,
        new ImageSelection(Enum.Parse<ImageSelectionKind>(row.GetString("SelectionKind")!),
            row.GetString("SelectionValue")!),
        row.GetInt32("StartOffset")!.Value, row.GetInt32("RequestedCount")!.Value,
        row.GetInt32("NextOffset")!.Value, row.GetString("Status")!,
        row.GetDateTimeOffset("CreatedAt")!.Value, row.GetInt32("FailedJobs") ?? 0,
        string.IsNullOrEmpty(row.GetString("Error")) ? null : row.GetString("Error"))
    {
        ManifestPath = string.IsNullOrEmpty(row.GetString("ManifestPath")) ? null : row.GetString("ManifestPath"),
        Width = row.GetInt32("Width") ?? 250,
        CopiedImages = row.GetInt32("CopiedImages") ?? 0,
        SkippedImages = row.GetInt32("SkippedImages") ?? 0,
        FailedImages = row.GetInt32("FailedImages") ?? 0
    };
}
