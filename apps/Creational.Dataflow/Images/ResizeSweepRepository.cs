using Azure;
using Azure.Data.Tables;

namespace Creational.Dataflow.Images;

public sealed record ResizeSweepRun(string Id, string Status, DateTimeOffset CreatedAt,
    string Cursor = "", int Processed = 0, int Generated = 0, int Skipped = 0,
    int Failed = 0, string? ExecutionId = null, bool ResweepRequested = false,
    string? Error = null)
{
    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed record ResizeFailure(string Title, int Width, string Error);

public interface IResizeSweepRepository
{
    Task<ResizeSweepRun> RequestOrJoinAsync(CancellationToken ct);
    Task<bool> TryClaimLaunchAsync(string id, CancellationToken ct);
    Task AddAsync(ResizeSweepRun run, CancellationToken ct);
    Task<ResizeSweepRun?> GetAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<ResizeSweepRun>> ListAsync(int limit, CancellationToken ct);
    Task<IReadOnlyList<ResizeFailure>> ListFailuresAsync(string id, int limit, CancellationToken ct);
    Task PatchAsync(string id, string? status = null, string? cursor = null,
        int? processed = null, string? executionId = null, bool? resweepRequested = null,
        string? error = null, CancellationToken ct = default);
}

public sealed class TableResizeSweepRepository(TableClient table) : IResizeSweepRepository
{
    private const string Partition = "__resize_runs";
    private const string ControlPartition = "__resize_control";

    public async Task<ResizeSweepRun> RequestOrJoinAsync(CancellationToken ct)
    {
        while (true)
        {
            var markerResponse = await table.GetEntityIfExistsAsync<TableEntity>(
                ControlPartition, "active", cancellationToken: ct);
            if (!markerResponse.HasValue)
            {
                var created = new ResizeSweepRun(Guid.NewGuid().ToString("N"), "pending", DateTimeOffset.UtcNow);
                try
                {
                    await table.AddEntityAsync(new TableEntity(ControlPartition, "active")
                    {
                        ["RunId"] = created.Id, ["CreatedAt"] = created.CreatedAt
                    }, ct);
                    await AddAsync(created, ct);
                    return created;
                }
                catch (RequestFailedException error) when (error.Status == 409) { continue; }
            }
            var marker = markerResponse.Value!;
            var activeId = marker.GetString("RunId")!;
            var active = await GetAsync(activeId, ct);
            if (active is null)
            {
                var recovered = new ResizeSweepRun(activeId, "pending",
                    marker.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.UtcNow);
                try { await AddAsync(recovered, ct); return recovered; }
                catch (RequestFailedException error) when (error.Status == 409) { continue; }
            }
            if (active.Status is "pending" or "running" or "continuing" ||
                (active.Status is "complete" or "complete-with-errors" && active.ExecutionId is not null))
            {
                if (active.ExecutionId is not null || active.Status is "running" or "continuing")
                    await PatchAsync(active.Id, resweepRequested: true, ct: ct);
                return active;
            }
            var next = new ResizeSweepRun(Guid.NewGuid().ToString("N"), "pending", DateTimeOffset.UtcNow);
            marker["RunId"] = next.Id;
            marker["CreatedAt"] = next.CreatedAt;
            try
            {
                await table.UpdateEntityAsync(marker, marker.ETag, TableUpdateMode.Replace, ct);
                await AddAsync(next, ct);
                if (active.ResweepRequested)
                    await PatchAsync(active.Id, resweepRequested: false, ct: ct);
                return next;
            }
            catch (RequestFailedException error) when (error.Status is 409 or 412) { continue; }
        }
    }

    public async Task<bool> TryClaimLaunchAsync(string id, CancellationToken ct)
    {
        var response = await table.GetEntityIfExistsAsync<TableEntity>(Partition, id, cancellationToken: ct);
        if (!response.HasValue) return false;
        var row = response.Value!;
        if (row.GetString("Status") is not ("pending" or "continuing") ||
            !string.IsNullOrEmpty(row.GetString("ExecutionId"))) return false;
        var patch = new TableEntity(Partition, id) { ["Status"] = "running", ["Error"] = "" };
        try
        {
            await table.UpdateEntityAsync(patch, row.ETag, TableUpdateMode.Merge, ct);
            return true;
        }
        catch (RequestFailedException error) when (error.Status is 409 or 412) { return false; }
    }

    public async Task AddAsync(ResizeSweepRun run, CancellationToken ct) =>
        await table.AddEntityAsync(new TableEntity(Partition, run.Id)
        {
            ["Status"] = run.Status, ["CreatedAt"] = run.CreatedAt,
            ["Cursor"] = run.Cursor, ["Processed"] = run.Processed,
            ["Generated"] = run.Generated, ["Skipped"] = run.Skipped,
            ["Failed"] = run.Failed, ["ExecutionId"] = run.ExecutionId ?? "",
            ["ResweepRequested"] = run.ResweepRequested, ["Error"] = run.Error ?? ""
        }, ct);

    public async Task<ResizeSweepRun?> GetAsync(string id, CancellationToken ct)
    {
        var response = await table.GetEntityIfExistsAsync<TableEntity>(Partition, id, cancellationToken: ct);
        return response.HasValue ? Read(response.Value!) : null;
    }

    public async Task<IReadOnlyList<ResizeSweepRun>> ListAsync(int limit, CancellationToken ct)
    {
        var rows = new List<ResizeSweepRun>();
        await foreach (var entity in table.QueryAsync<TableEntity>(
            filter: "PartitionKey eq '__resize_runs'", cancellationToken: ct)) rows.Add(Read(entity));
        return rows.OrderByDescending(x => x.CreatedAt).Take(limit).ToArray();
    }

    public async Task<IReadOnlyList<ResizeFailure>> ListFailuresAsync(string id, int limit, CancellationToken ct)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid resize run ID", nameof(id));
        var rows = new List<ResizeFailure>();
        var prefix = id + "-";
        await foreach (var row in table.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '__resize_failures' and RowKey ge '{prefix}' and RowKey lt '{id}.'",
            maxPerPage: Math.Min(limit, 1000), cancellationToken: ct))
        {
            rows.Add(new ResizeFailure(row.GetString("Title") ?? "", row.GetInt32("Width") ?? 0,
                row.GetString("Error") ?? ""));
            if (rows.Count >= limit) break;
        }
        return rows;
    }

    public async Task PatchAsync(string id, string? status = null, string? cursor = null,
        int? processed = null, string? executionId = null, bool? resweepRequested = null,
        string? error = null, CancellationToken ct = default)
    {
        var entity = new TableEntity(Partition, id);
        if (status is not null) entity["Status"] = status;
        if (cursor is not null) entity["Cursor"] = cursor;
        if (processed is not null) entity["Processed"] = processed;
        if (executionId is not null) entity["ExecutionId"] = executionId;
        if (resweepRequested is not null) entity["ResweepRequested"] = resweepRequested;
        if (error is not null) entity["Error"] = error;
        await table.UpsertEntityAsync(entity, TableUpdateMode.Merge, ct);
    }

    private static ResizeSweepRun Read(TableEntity row) => new(
        row.RowKey, row.GetString("Status") ?? "pending",
        row.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.MinValue,
        row.GetString("Cursor") ?? "", row.GetInt32("Processed") ?? 0,
        row.GetInt32("Generated") ?? 0, row.GetInt32("Skipped") ?? 0,
        row.GetInt32("Failed") ?? 0, Empty(row.GetString("ExecutionId")),
        row.GetBoolean("ResweepRequested") ?? false, Empty(row.GetString("Error")))
        { UpdatedAt = row.Timestamp };

    private static string? Empty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
