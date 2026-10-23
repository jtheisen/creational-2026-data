using System.Text.Json;
using Azure;
using Azure.Data.Tables;

namespace Creational.Dataflow.Images;

public interface IThumbnailImportRepository
{
    Task AddJobAsync(ThumbnailImportJob job, CancellationToken ct);
    Task<ThumbnailImportJob?> GetJobAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<ThumbnailImportJob>> ListIncompleteJobsAsync(CancellationToken ct);
    Task SaveJobAsync(ThumbnailImportJob job, CancellationToken ct);
    Task<ThumbnailRendition?> GetRenditionAsync(string title, int width, CancellationToken ct);
    Task SaveRenditionAsync(ThumbnailRendition rendition, CancellationToken ct);
    Task EnsureAssetAsync(string title, CancellationToken ct);
}

public sealed class ThumbnailImportRepository(TableClient table) : IThumbnailImportRepository
{
    private const string Jobs = "__thumbnail_jobs";
    private const string Renditions = "__thumbnail_renditions";
    private const string Assets = "__thumbnail_assets";

    public async Task AddJobAsync(ThumbnailImportJob job, CancellationToken ct) =>
        await table.AddEntityAsync(JobEntity(job), ct);

    public async Task<ThumbnailImportJob?> GetJobAsync(string id, CancellationToken ct)
    {
        var response = await table.GetEntityIfExistsAsync<TableEntity>(Jobs, id, cancellationToken: ct);
        return response.HasValue ? ReadJob(response.Value!) : null;
    }

    public async Task<IReadOnlyList<ThumbnailImportJob>> ListIncompleteJobsAsync(CancellationToken ct)
    {
        var result = new List<ThumbnailImportJob>();
        await foreach (var row in table.QueryAsync<TableEntity>(x => x.PartitionKey == Jobs,
            cancellationToken: ct))
        {
            var job = ReadJob(row);
            if (job.Status is "pending" or "running") result.Add(job);
        }
        return result.OrderBy(x => x.CreatedAt).ToArray();
    }

    public async Task SaveJobAsync(ThumbnailImportJob job, CancellationToken ct) =>
        await table.UpsertEntityAsync(JobEntity(job), TableUpdateMode.Replace, ct);

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

    private static TableEntity JobEntity(ThumbnailImportJob job) => new(Jobs, job.Id)
    {
        ["Width"] = job.Width, ["Titles"] = JsonSerializer.Serialize(job.Titles),
        ["Status"] = job.Status, ["CreatedAt"] = job.CreatedAt,
        ["Error"] = job.Error ?? ""
    };

    private static ThumbnailImportJob ReadJob(TableEntity row) => new(row.RowKey,
        row.GetInt32("Width")!.Value,
        JsonSerializer.Deserialize<string[]>(row.GetString("Titles")!)!,
        row.GetString("Status")!, row.GetDateTimeOffset("CreatedAt")!.Value,
        string.IsNullOrEmpty(row.GetString("Error")) ? null : row.GetString("Error"));
}
