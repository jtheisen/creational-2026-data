using System.Text.Json;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Creational.Dataflow.Components;
using Creational.Dataflow.Execution;
using Creational.Dataflow.Images;
using Creational.Dataflow.Storage;
using Creational.Dataflow.Sql;
using Creational.Dataflow.Targets;
using Creational.Dataflow.Transfers;
using MudBlazor.Services;

var accountUrl = Environment.GetEnvironmentVariable("CREATIONAL_STORAGE_ACCOUNT_URL")
    ?? "https://creational.blob.core.windows.net";
var credential = new DefaultAzureCredential();
var blobService = new BlobServiceClient(new Uri(accountUrl), credential);
var blobStore = new AzureBlobStore(blobService);

if (args.Length == 1 && args[0] == "--sql-capacity")
{
    var connectionString = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Set CREATIONAL_SQL_CONNECTION_STRING for SQL capacity inspection.");
    var schema = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CATALOG_SCHEMA") ?? "catalog";
    await using var connection = await new SqlConnectionFactory(connectionString).OpenAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(await SqlCatalogCapacity.ReadAsync(connection, schema,
        CancellationToken.None, message => Console.Error.WriteLine(message))));
    return;
}

if (args.Length == 1 && args[0] == "--migrate-sql-utf8")
{
    var connectionString = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Set CREATIONAL_SQL_CONNECTION_STRING for SQL migration.");
    var schema = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CATALOG_SCHEMA") ?? "catalog";
    await using var connection = await new SqlConnectionFactory(connectionString).OpenAsync(CancellationToken.None);
    var steps = SqlCatalogSchema.RenderUtf8ConversionSteps(schema);
    for (var i = 0; i < steps.Count; i++)
    {
        Console.Error.WriteLine($"UTF-8 migration step {i + 1}/{steps.Count}");
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(steps[i], connection)
        { CommandTimeout = 1800 };
        await command.ExecuteNonQueryAsync();
    }
    Console.WriteLine($"Completed {steps.Count} UTF-8 migration steps.");
    return;
}

if (args.Length == 1 && args[0] == "--migrate-sql-page-compression")
{
    var connectionString = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Set CREATIONAL_SQL_CONNECTION_STRING for SQL migration.");
    var schema = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CATALOG_SCHEMA") ?? "catalog";
    await using var connection = await new SqlConnectionFactory(connectionString).OpenAsync(CancellationToken.None);
    var steps = SqlCatalogSchema.RenderPageCompressionSteps(schema);
    for (var i = 0; i < steps.Count; i++)
    {
        Console.Error.WriteLine($"PAGE compression step {i + 1}/{steps.Count}");
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(steps[i], connection)
        { CommandTimeout = 1800 };
        await command.ExecuteNonQueryAsync();
    }
    Console.WriteLine($"Completed {steps.Count} PAGE-compression steps.");
    return;
}

if (args.Length == 1 && args[0] == "--migrate-sql-compact-images")
{
    var connectionString = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Set CREATIONAL_SQL_CONNECTION_STRING for SQL migration.");
    var schema = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CATALOG_SCHEMA") ?? "catalog";
    await using var connection = await new SqlConnectionFactory(connectionString).OpenAsync(CancellationToken.None);
    await using var command = new Microsoft.Data.SqlClient.SqlCommand(SqlCatalogSchema.RenderCompactImages(schema), connection)
    { CommandTimeout = 300 };
    await command.ExecuteNonQueryAsync();
    Console.WriteLine("Compact image schema is current.");
    return;
}

if (args.Length == 1 && args[0] == "--migrate-sql-operational")
{
    var connectionString = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Set CREATIONAL_SQL_CONNECTION_STRING for SQL migration.");
    var schema = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CATALOG_SCHEMA") ?? "catalog";
    await using var connection = await new SqlConnectionFactory(connectionString).OpenAsync(CancellationToken.None);
    await using var command = new Microsoft.Data.SqlClient.SqlCommand(SqlCatalogSchema.RenderOperationalState(schema), connection)
    { CommandTimeout = 300 };
    await command.ExecuteNonQueryAsync();
    Console.WriteLine("Operational state schema is current.");
    return;
}

if (args.Length == 1 && args[0] == "--migrate-sql-image-work")
{
    var connectionString = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Set CREATIONAL_SQL_CONNECTION_STRING for SQL migration.");
    var schema = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CATALOG_SCHEMA") ?? "catalog";
    await using var connection = await new SqlConnectionFactory(connectionString).OpenAsync(CancellationToken.None);
    await using var command = new Microsoft.Data.SqlClient.SqlCommand(SqlCatalogSchema.RenderImageWork(schema), connection)
    { CommandTimeout = 300 };
    await command.ExecuteNonQueryAsync();
    Console.WriteLine("Image work schema is current; all work kinds default to disabled.");
    return;
}

if (args.Length == 1 && args[0] == "--migrate-sql-image-adoption")
{
    var connectionString = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Set CREATIONAL_SQL_CONNECTION_STRING for SQL migration.");
    var schema = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CATALOG_SCHEMA") ?? "catalog";
    await using var connection = await new SqlConnectionFactory(connectionString).OpenAsync(CancellationToken.None);
    await using var command = new Microsoft.Data.SqlClient.SqlCommand(SqlCatalogSchema.RenderImageAdoption(schema), connection)
    { CommandTimeout = 300 };
    await command.ExecuteNonQueryAsync();
    Console.WriteLine("Image adoption schema is current.");
    return;
}

if (args.Length == 1 && args[0] == "--migrate-sql-resize-batch")
{
    var connectionString = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Set CREATIONAL_SQL_CONNECTION_STRING for SQL migration.");
    var schema = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CATALOG_SCHEMA") ?? "catalog";
    await using var connection = await new SqlConnectionFactory(connectionString).OpenAsync(CancellationToken.None);
    await using var command = new Microsoft.Data.SqlClient.SqlCommand(SqlCatalogSchema.RenderResizeBatch(schema), connection)
    { CommandTimeout = 300 };
    await command.ExecuteNonQueryAsync();
    Console.WriteLine("Resize batch schema is current.");
    return;
}

if (args.Length == 1 && args[0] == "--migrate-sql-source-generation")
{
    var connectionString = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Set CREATIONAL_SQL_CONNECTION_STRING for SQL migration.");
    var schema = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CATALOG_SCHEMA") ?? "catalog";
    await using var connection = await new SqlConnectionFactory(connectionString).OpenAsync(CancellationToken.None);
    await using var command = new Microsoft.Data.SqlClient.SqlCommand(SqlCatalogSchema.RenderSourceGeneration(schema), connection)
    { CommandTimeout = 300 };
    await command.ExecuteNonQueryAsync();
    Console.WriteLine("Source generation schema is current.");
    return;
}

if (args.Length == 1 && args[0] == "--migrate-sql-pending-rendition-indexes")
{
    var connectionString = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Set CREATIONAL_SQL_CONNECTION_STRING for SQL migration.");
    var schema = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CATALOG_SCHEMA") ?? "catalog";
    await using var connection = await new SqlConnectionFactory(connectionString).OpenAsync(CancellationToken.None);
    await using var command = new Microsoft.Data.SqlClient.SqlCommand(
        SqlCatalogSchema.RenderPendingRenditionIndexes(schema), connection) { CommandTimeout = 300 };
    await command.ExecuteNonQueryAsync();
    Console.WriteLine("Pending rendition indexes are current.");
    return;
}

if (args.Length == 1 && args[0] == "--migrate-sql-focused-image-priority")
{
    var connectionString = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Set CREATIONAL_SQL_CONNECTION_STRING for SQL migration.");
    var schema = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CATALOG_SCHEMA") ?? "catalog";
    await using var connection = await new SqlConnectionFactory(connectionString).OpenAsync(CancellationToken.None);
    await using var command = new Microsoft.Data.SqlClient.SqlCommand(
        SqlCatalogSchema.RenderFocusedImagePriority(schema), connection) { CommandTimeout = 300 };
    await command.ExecuteNonQueryAsync();
    Console.WriteLine("Focused image priority index is current.");
    return;
}

if (args.Length == 3 && args[0] == "--adopt-sql-images" && args[1] is "--verify-only" or "--apply"
    && int.TryParse(args[2], out var adoptionLimit))
{
    var connectionString = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Set CREATIONAL_SQL_CONNECTION_STRING for image adoption.");
    var schema = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CATALOG_SCHEMA") ?? "catalog";
    var table = new TableServiceClient(new Uri(accountUrl.Replace(".blob.", ".table.",
        StringComparison.OrdinalIgnoreCase)), credential).GetTableClient("CollectionTargets");
    var adopter = new SqlImageAdopter(table, new SqlConnectionFactory(connectionString), schema,
        new SqlImageBlobStore(blobService, credential));
    for (var retry = 0; ; retry++)
    {
        try
        {
            Console.WriteLine(JsonSerializer.Serialize(await adopter.RunAsync(adoptionLimit,
                args[1] == "--apply", CancellationToken.None)));
            break;
        }
        catch (Exception error) when (retry < 19 && TransientAdoptionError(error))
        {
            var delay = TimeSpan.FromSeconds(Math.Min(60, 5 * (retry + 1)));
            Console.Error.WriteLine($"Adoption request failed transiently ({error.GetBaseException().Message}); " +
                $"resuming from the last checkpoint in {delay.TotalSeconds:0} seconds.");
            await Task.Delay(delay);
        }
    }
    return;
}

if (args.Length == 2 && args[0] == "--sql-resize-canary-manifest" && int.TryParse(args[1], out var canaryImageId))
{
    var batchId = Guid.NewGuid();
    var path = await new SqlImageBlobStore(blobService, credential).WriteResizeManifestAsync(batchId,
        [new ImageWorkClaim(canaryImageId, ImageWorkKind.Resize, 0, batchId, 1, "", "")],
        CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(new { batchId, manifest = path }));
    return;
}

if (args.Length == 1 && args[0] == "--sql-adoption-issues")
{
    var connectionString = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Set CREATIONAL_SQL_CONNECTION_STRING for adoption inspection.");
    var schema = SqlCatalogSchema.ValidateName(Environment.GetEnvironmentVariable("CREATIONAL_SQL_CATALOG_SCHEMA") ?? "catalog");
    await using var connection = await new SqlConnectionFactory(connectionString).OpenAsync(CancellationToken.None);
    await using var command = new Microsoft.Data.SqlClient.SqlCommand($"""
        SELECT TOP (30) PartitionName,LegacyRowKey,Reason,Title
        FROM [{schema}].[ImageAdoptionIssue] ORDER BY NotedAt DESC
        """, connection);
    var issues = new List<object>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) issues.Add(new { partition = reader.GetString(0), key = reader.GetString(1),
        reason = reader.GetString(2), title = reader.IsDBNull(3) ? null : reader.GetString(3) });
    Console.WriteLine(JsonSerializer.Serialize(issues));
    return;
}

if (args.Length == 1 && args[0] == "--reset-sql-image-adoption-checkpoints")
{
    var connectionString = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Set CREATIONAL_SQL_CONNECTION_STRING for adoption maintenance.");
    var schema = SqlCatalogSchema.ValidateName(Environment.GetEnvironmentVariable("CREATIONAL_SQL_CATALOG_SCHEMA") ?? "catalog");
    await using var connection = await new SqlConnectionFactory(connectionString).OpenAsync(CancellationToken.None);
    await using var command = new Microsoft.Data.SqlClient.SqlCommand(
        $"DELETE FROM [{schema}].[ImageAdoptionCheckpoint]", connection);
    Console.WriteLine($"Cleared {await command.ExecuteNonQueryAsync()} image-adoption checkpoints; verified assets remain.");
    return;
}

if (args.Length >= 1 && args[0] is "--sql-image-work-setting" or "--sql-enqueue-pending" or
    "--sql-prioritize-ott" or "--sql-prioritize-image" or "--sql-mark-image-metadata-stale" or
    "--sql-work-status")
{
    var connectionString = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Set CREATIONAL_SQL_CONNECTION_STRING for SQL image work.");
    var schema = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CATALOG_SCHEMA") ?? "catalog";
    var work = new SqlImageWorkRepository(new SqlConnectionFactory(connectionString), schema);
    if (args[0] == "--sql-image-work-setting")
    {
        if (args.Length == 2 && Enum.TryParse<ImageWorkKind>(args[1], true, out var selected))
            Console.WriteLine(JsonSerializer.Serialize(await work.GetSettingAsync(selected, CancellationToken.None)));
        else if (args.Length == 6 && Enum.TryParse<ImageWorkKind>(args[1], true, out selected) &&
                 bool.TryParse(args[2], out var enabled) &&
                 float.TryParse(args[3], System.Globalization.CultureInfo.InvariantCulture, out var minimum) &&
                 byte.TryParse(args[4], out var concurrency) && int.TryParse(args[5], out var pacing))
        {
            await work.SetSettingAsync(new ImageWorkSetting(selected, enabled, minimum, concurrency, pacing),
                CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(await work.GetSettingAsync(selected, CancellationToken.None)));
        }
        else throw new ArgumentException("Usage: --sql-image-work-setting KIND [ENABLED MINIMUM CONCURRENCY PACING_MS]");
    }
    else if (args[0] == "--sql-work-status" && args.Length == 1)
        Console.WriteLine(JsonSerializer.Serialize(new { settings = await Task.WhenAll(
            work.GetSettingAsync(ImageWorkKind.Metadata, CancellationToken.None),
            work.GetSettingAsync(ImageWorkKind.Copy, CancellationToken.None),
            work.GetSettingAsync(ImageWorkKind.Resize, CancellationToken.None)),
            queue = (await work.QueueCountsAsync(CancellationToken.None))
                .Select(x => new { x.Kind, x.State, x.Count }),
            resizeBatches = await work.ListOpenResizeBatchesAsync(CancellationToken.None) }));
    else if (args[0] == "--sql-enqueue-pending" && args.Length == 3 &&
             Enum.TryParse<ImageWorkKind>(args[1], true, out var kind) && int.TryParse(args[2], out var count))
        Console.WriteLine(JsonSerializer.Serialize(new { kind, enqueued =
            await work.FillPendingQueueAsync(kind, count, CancellationToken.None) }));
    else if (args[0] == "--sql-prioritize-ott" && args.Length == 3 && int.TryParse(args[1], out var ott) &&
             float.TryParse(args[2], System.Globalization.CultureInfo.InvariantCulture, out var priority))
        Console.WriteLine(JsonSerializer.Serialize(new { ott, priority, images =
            await work.SetSubtreePriorityAsync(ott, priority, CancellationToken.None) }));
    else if (args[0] == "--sql-prioritize-image" && args.Length == 3 && int.TryParse(args[1], out var imageId) &&
             float.TryParse(args[2], System.Globalization.CultureInfo.InvariantCulture, out var imagePriority))
    {
        await work.SetImagePriorityAsync(imageId, imagePriority, CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(new { imageId, priority = imagePriority }));
    }
    else if (args[0] == "--sql-mark-image-metadata-stale" && args.Length == 2 &&
             int.TryParse(args[1], out var staleImageId))
    {
        await work.MarkMetadataStaleAsync(staleImageId, CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(new { imageId = staleImageId, metadataStale = true }));
    }
    else throw new ArgumentException("Invalid SQL image work command.");
    return;
}

if (args.Length == 2 && args[0] == "--adopt-sql-targets" && args[1] is "--verify-only" or "--apply")
{
    var connectionString = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Set CREATIONAL_SQL_CONNECTION_STRING for target adoption.");
    var schema = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CATALOG_SCHEMA") ?? "catalog";
    var source = new TableTargetRepository(new TableServiceClient(
        new Uri(accountUrl.Replace(".blob.", ".table.", StringComparison.OrdinalIgnoreCase)), credential)
        .GetTableClient("CollectionTargets"));
    var target = new SqlTargetRepository(new SqlConnectionFactory(connectionString), schema);
    var collection = October2026Collection.Collection;
    var oldStates = await source.GetCollectionAsync(collection, CancellationToken.None);
    var oldParallelism = await new TableCollectionSettingsRepository(new TableServiceClient(
        new Uri(accountUrl.Replace(".blob.", ".table.", StringComparison.OrdinalIgnoreCase)), credential)
        .GetTableClient("CollectionTargets")).GetParallelismAsync(collection, CancellationToken.None);
    var settings = new SqlCollectionSettingsRepository(new SqlConnectionFactory(connectionString), schema);
    if (args[1] == "--apply")
    {
        foreach (var state in oldStates.Values)
            await target.ImportIfMissingAsync(state, CancellationToken.None);
        await settings.SetParallelismAsync(collection, oldParallelism, CancellationToken.None);
    }
    var sqlStates = await target.GetCollectionAsync(collection, CancellationToken.None);
    var missing = oldStates.Keys.Except(sqlStates.Keys).Select(x => x.ToString()).ToArray();
    var mismatched = oldStates.Where(pair => sqlStates.TryGetValue(pair.Key, out var state) &&
        JsonSerializer.Serialize(pair.Value) != JsonSerializer.Serialize(state)).Select(pair => pair.Key.ToString()).ToArray();
    Console.WriteLine(JsonSerializer.Serialize(new { mode = args[1], source = oldStates.Count, sql = sqlStates.Count,
        missing, mismatched, parallelism = await settings.GetParallelismAsync(collection, CancellationToken.None), oldParallelism }));
    if (missing.Length > 0 || mismatched.Length > 0) Environment.ExitCode = 2;
    return;
}

if (args.Length == 1 && args[0] == "--backfill-sql-renditions")
{
    var connectionString = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Set CREATIONAL_SQL_CONNECTION_STRING for SQL backfill.");
    var schema = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CATALOG_SCHEMA") ?? "catalog";
    await using var connection = await new SqlConnectionFactory(connectionString).OpenAsync(CancellationToken.None);
    var loader = new SqlCatalogLoader(new SqlConnectionFactory(connectionString), schema);
    var inserted = await loader.EnsureRenditionsAsync(connection, 1_000, CancellationToken.None,
        (imageId, rows) =>
        {
            if (imageId % 20_000 < 1_000)
                Console.Error.WriteLine($"Rendition backfill reached ImageId {imageId}, inserted {rows} rows.");
        });
    Console.WriteLine($"Inserted {inserted} missing rendition rows.");
    return;
}

if (args.Length == 1 && args[0] == "--trim-sql-image-hashes")
{
    var connectionString = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Set CREATIONAL_SQL_CONNECTION_STRING for SQL hash maintenance.");
    var schema = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CATALOG_SCHEMA") ?? "catalog";
    await using var connection = await new SqlConnectionFactory(connectionString).OpenAsync(CancellationToken.None);
    var loader = new SqlCatalogLoader(new SqlConnectionFactory(connectionString), schema);
    var updated = await loader.TrimSourceHashesAsync(connection, 1_000, CancellationToken.None,
        (imageId, rows) =>
        {
            if (imageId % 20_000 < 1_000)
                Console.Error.WriteLine($"Hash trim reached ImageId {imageId}, updated {rows} rows.");
        });
    Console.WriteLine($"Trimmed {updated} padded source hashes.");
    return;
}

if (args.Length == 3 && args[0] == "--sql-estimate-page" && int.TryParse(args[2], out var indexId))
{
    var connectionString = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Set CREATIONAL_SQL_CONNECTION_STRING for SQL compression estimates.");
    var schema = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CATALOG_SCHEMA") ?? "catalog";
    await using var connection = await new SqlConnectionFactory(connectionString).OpenAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(await SqlCatalogCapacity.EstimatePageAsync(connection, schema,
        args[1], indexId, CancellationToken.None)));
    return;
}

if (args.Length == 3 && args[0] == "--build-ott-edges")
{
    await OttEdges.BuildAsync(args[1], args[2], CancellationToken.None);
    Console.WriteLine($"Wrote {args[2]}");
    return;
}

if (args.Length == 7 && args[0] == "--load-sql-catalog")
{
    var connectionString = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Set CREATIONAL_SQL_CONNECTION_STRING for catalog loading.");
    var schema = Environment.GetEnvironmentVariable("CREATIONAL_SQL_CATALOG_SCHEMA") ?? "catalog";
    var batchSize = int.TryParse(Environment.GetEnvironmentVariable("CREATIONAL_SQL_CATALOG_BATCH_SIZE"),
        out var configuredBatchSize) ? configuredBatchSize : 5_000;
    var shards = await new SourceManifestLoader(blobStore).LoadAsync(CancellationToken.None);
    var loader = new SqlCatalogLoader(new SqlConnectionFactory(connectionString), schema);
    var result = await loader.LoadAsync(args[1], args[2], short.Parse(args[3]), args[4],
        short.Parse(args[5]), args[6], batchSize, shards.Select(x => x.Key).ToArray(), CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(result));
    return;
}

if (args.Length == 2 && args[0] == "--adopt-existing" && args[1] is "--verify-only" or "--apply")
{
    var tableUrl = accountUrl.Replace(".blob.", ".table.", StringComparison.OrdinalIgnoreCase);
    var tableService = new TableServiceClient(new Uri(tableUrl), credential);
    var table = tableService.GetTableClient("CollectionTargets");
    if (args[1] == "--apply") await table.CreateIfNotExistsAsync();
    var shards = await new SourceManifestLoader(blobStore).LoadAsync(CancellationToken.None);
    var definitions = October2026Collection.Build(shards);
    var adopter = new ExistingAssetAdopter(new TableTargetRepository(table),
        new RemoteManifestCatalog(blobStore, shards), new TargetVerifier(blobStore));
    var result = args[1] == "--apply"
        ? await adopter.AdoptAsync(definitions, CancellationToken.None)
        : await adopter.VerifyOnlyAsync(definitions, CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(new { mode = args[1], targets = definitions.Count, result.Adopted,
        result.AlreadyCurrent, result.Failed }));
    return;
}

if (args.Length == 2 && args[0] == "--import-thumbnail-once")
{
    var tableService = new TableServiceClient(new Uri(accountUrl.Replace(".blob.", ".table.", StringComparison.OrdinalIgnoreCase)), credential);
    var repository = new ThumbnailImportRepository(tableService.GetTableClient("CollectionTargets"));
    var userAgent = Environment.GetEnvironmentVariable("Copies__UserAgent") ??
        "CreationalDataflow/1.0 (https://github.com/jtheisen/creational-2026-data)";
    var pacer = new WikimediaRequestPacer();
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    var service = new ThumbnailImportService(repository, new ThumbnailBlobCopier(http, blobService, pacer),
        new WikimediaImageInfo(http, userAgent, pacer),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<ThumbnailImportService>.Instance);
    var id = await service.CreateJobAsync([args[1]], CancellationToken.None);
    await service.ProcessJobAsync(id, CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(new { id, job = await service.GetJobAsync(id, CancellationToken.None),
        renditions = await service.GetRenditionsAsync((await service.GetJobAsync(id, CancellationToken.None))!, CancellationToken.None) }));
    return;
}

if (args.Length == 4 && args[0] == "--schedule-image-batch")
{
    var selection = ImageSelection.Parse(args[1]);
    var tableService = new TableServiceClient(new Uri(accountUrl.Replace(".blob.", ".table.", StringComparison.OrdinalIgnoreCase)), credential);
    var table = tableService.GetTableClient("CollectionTargets");
    var shards = await new SourceManifestLoader(blobStore).LoadAsync(CancellationToken.None);
    var catalog = new ImageCatalog(new TableTargetRepository(table),
        new AzureOutputManifestReader(blobStore), blobStore, shards);
    var userAgent = Environment.GetEnvironmentVariable("Copies__UserAgent") ??
        "CreationalDataflow/1.0 (https://github.com/jtheisen/creational-2026-data)";
    var pacer = new WikimediaRequestPacer();
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    var service = new ThumbnailImportService(new ThumbnailImportRepository(table),
        new ThumbnailBlobCopier(http, blobService, pacer), new WikimediaImageInfo(http, userAgent, pacer),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<ThumbnailImportService>.Instance,
        catalog, new AzureThumbnailBatchManifestStore(blobService));
    var id = await service.CreateBatchAsync(selection, int.Parse(args[2]), int.Parse(args[3]), CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(await service.GetBatchAsync(id, CancellationToken.None)));
    return;
}

if (args.Length == 2 && args[0] == "--schedule-image-run")
{
    var selection = ImageSelection.Parse(args[1]);
    var tableService = new TableServiceClient(new Uri(accountUrl.Replace(".blob.", ".table.", StringComparison.OrdinalIgnoreCase)), credential);
    var table = tableService.GetTableClient("CollectionTargets");
    var shards = await new SourceManifestLoader(blobStore).LoadAsync(CancellationToken.None);
    var catalog = new ImageCatalog(new TableTargetRepository(table),
        new AzureOutputManifestReader(blobStore), blobStore, shards);
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    var pacer = new WikimediaRequestPacer();
    var service = new ThumbnailImportService(new ThumbnailImportRepository(table),
        new ThumbnailBlobCopier(http, blobService, pacer), new WikimediaImageInfo(http, null, pacer),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<ThumbnailImportService>.Instance,
        catalog, new AzureThumbnailBatchManifestStore(blobService));
    var id = await service.CreateRunAsync(selection, CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(await service.GetRunAsync(id, CancellationToken.None)));
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddSingleton<TokenCredential>(credential);
builder.Services.AddSingleton(blobService);
builder.Services.AddSingleton(blobStore);
builder.Services.AddSingleton<IBlobStore>(blobStore);
builder.Services.AddSingleton<TargetVerifier>();
builder.Services.AddSingleton<TargetPlanner>();
builder.Services.AddSingleton<SourceManifestLoader>();
var webShards = await new SourceManifestLoader(blobStore).LoadAsync(CancellationToken.None);
builder.Services.AddSingleton<IReadOnlyList<ManifestShard>>(webShards);
var webTableService = new TableServiceClient(new Uri(accountUrl.Replace(".blob.", ".table.", StringComparison.OrdinalIgnoreCase)), credential);
builder.Services.AddSingleton(webTableService.GetTableClient("CollectionTargets"));
var sqlOperational = builder.Configuration.GetValue<bool>("SqlOperational:Enabled");
if (sqlOperational)
{
    var connectionString = builder.Configuration["SqlOperational:ConnectionString"]
        ?? Environment.GetEnvironmentVariable("CREATIONAL_SQL_CONNECTION_STRING")
        ?? throw new InvalidOperationException("SQL operational state enabled without a connection string.");
    var schema = builder.Configuration["SqlOperational:Schema"] ?? "catalog";
    builder.Services.AddSingleton(new SqlConnectionFactory(connectionString));
    builder.Services.AddSingleton<ITargetRepository>(sp => new SqlTargetRepository(
        sp.GetRequiredService<SqlConnectionFactory>(), schema));
    builder.Services.AddSingleton<ICollectionSettingsRepository>(sp => new SqlCollectionSettingsRepository(
        sp.GetRequiredService<SqlConnectionFactory>(), schema));
}
else
{
    builder.Services.AddSingleton<ITargetRepository, TableTargetRepository>();
    builder.Services.AddSingleton<ICollectionSettingsRepository, TableCollectionSettingsRepository>();
}
builder.Services.AddSingleton<IOutputManifestReader, AzureOutputManifestReader>();
builder.Services.AddSingleton<ImageCatalog>();
builder.Services.AddSingleton<IImageCatalog>(sp => sp.GetRequiredService<ImageCatalog>());
builder.Services.AddSingleton(sp => new WorkInputBuilder(sp.GetRequiredService<IOutputManifestReader>(),
    webShards, blobStore));
var queueUrl = accountUrl.Replace(".blob.", ".queue.", StringComparison.OrdinalIgnoreCase) + "/collection-work";
var queueClient = new QueueClient(new Uri(queueUrl), credential,
    new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
builder.Services.AddSingleton(queueClient);
builder.Services.AddSingleton<TransferLedger>();
builder.Services.AddSingleton<IBlockBlobWriter, AzureBlockBlobWriter>();
var copyUserAgent = builder.Configuration["Copies:UserAgent"];
builder.Services.AddSingleton<WikimediaRequestPacer>();
builder.Services.AddSingleton(sp => new WikimediaImageInfo(new HttpClient(), copyUserAgent ??
    "CreationalDataflow/1.0 (https://github.com/jtheisen/creational-2026-data)",
    sp.GetRequiredService<WikimediaRequestPacer>()));
builder.Services.AddSingleton<IThumbnailImportRepository, ThumbnailImportRepository>();
builder.Services.AddSingleton<IThumbnailBatchManifestStore, AzureThumbnailBatchManifestStore>();
builder.Services.AddSingleton<IImageInfoSnapshotStore, AzureImageInfoSnapshotStore>();
builder.Services.AddSingleton<IImageInfoStore, ImageInfoStore>();
builder.Services.AddSingleton<IResizeSweepRepository, TableResizeSweepRepository>();
var sqlImageWork = sqlOperational && builder.Configuration.GetValue<bool>("SqlOperational:ImageEnabled");
builder.Services.AddSingleton(sp => new ResizeSweepService(
    sp.GetRequiredService<IResizeSweepRepository>(), sp.GetRequiredService<IResizeJobLauncher>(),
    sp.GetRequiredService<ILogger<ResizeSweepService>>(), !sqlImageWork));
builder.Services.AddSingleton<IResizeSweepScheduler>(sp => sp.GetRequiredService<ResizeSweepService>());
if (!sqlImageWork) builder.Services.AddHostedService(sp => sp.GetRequiredService<ResizeSweepService>());
builder.Services.AddSingleton<IThumbnailBlobCopier>(sp =>
{
    var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    http.DefaultRequestHeaders.UserAgent.ParseAdd(copyUserAgent ??
        "CreationalDataflow/1.0 (https://github.com/jtheisen/creational-2026-data)");
    return new ThumbnailBlobCopier(http, blobService, sp.GetRequiredService<WikimediaRequestPacer>());
});
builder.Services.AddSingleton(sp => new ThumbnailImportService(
    sp.GetRequiredService<IThumbnailImportRepository>(),
    sp.GetRequiredService<IThumbnailBlobCopier>(),
    sp.GetRequiredService<WikimediaImageInfo>(),
    sp.GetRequiredService<ILogger<ThumbnailImportService>>(),
    sp.GetRequiredService<IImageCatalog>(),
    sp.GetRequiredService<IThumbnailBatchManifestStore>(),
    sp.GetRequiredService<IImageInfoStore>(),
    sp.GetRequiredService<IResizeSweepScheduler>(), !sqlImageWork));
if (!sqlImageWork) builder.Services.AddHostedService(sp => sp.GetRequiredService<ThumbnailImportService>());
if (sqlImageWork)
{
    var schema = builder.Configuration["SqlOperational:Schema"] ?? "catalog";
    builder.Services.AddSingleton(sp => new SqlImageWorkRepository(
        sp.GetRequiredService<SqlConnectionFactory>(), schema));
    builder.Services.AddSingleton(new SqlImageBlobStore(blobService, credential));
    builder.Services.AddSingleton(sp =>
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        http.DefaultRequestHeaders.UserAgent.ParseAdd(copyUserAgent ??
            "CreationalDataflow/1.0 (https://github.com/jtheisen/creational-2026-data)");
        return new ThumbnailBlobCopier(http, blobService,
            sp.GetRequiredService<WikimediaRequestPacer>(), "images");
    });
    builder.Services.AddHostedService<SqlImageWorkService>();
}
builder.Services.AddSingleton<IStreamingCopier>(sp =>
{
    if (string.IsNullOrWhiteSpace(copyUserAgent))
        return new UnavailableCopier("Set Copies:UserAgent to a descriptive Wikimedia User-Agent with contact information");
    var http = new HttpClient();
    http.DefaultRequestHeaders.UserAgent.ParseAdd(copyUserAgent);
    return new StreamingBlobCopier(http, sp.GetRequiredService<IBlockBlobWriter>(),
        sp.GetRequiredService<TransferLedger>());
});
builder.Services.AddSingleton<CopyHostedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CopyHostedService>());
var jobResourceId = builder.Configuration["ContainerJob:ResourceId"];
var jobImage = builder.Configuration["ContainerJob:Image"];
if (!string.IsNullOrWhiteSpace(jobResourceId) && !string.IsNullOrWhiteSpace(jobImage))
{
    builder.Services.AddSingleton(sp => new ContainerAppsJobLauncher(
        new HttpClient(), credential, new ContainerJobOptions(jobResourceId, jobImage,
            builder.Configuration["ContainerJob:ContainerName"] ?? "collection-worker")));
    builder.Services.AddSingleton<IComputeJobLauncher>(sp => sp.GetRequiredService<ContainerAppsJobLauncher>());
    builder.Services.AddSingleton<IResizeJobLauncher>(sp => sp.GetRequiredService<ContainerAppsJobLauncher>());
    builder.Services.AddHostedService<QueueWorkDispatcher>();
    if (sqlImageWork) builder.Services.AddHostedService<SqlResizeBatchDispatcher>();
}
else
{
    builder.Services.AddSingleton<UnavailableJobLauncher>();
    builder.Services.AddSingleton<IComputeJobLauncher>(sp => sp.GetRequiredService<UnavailableJobLauncher>());
    builder.Services.AddSingleton<IResizeJobLauncher>(sp => sp.GetRequiredService<UnavailableJobLauncher>());
}
builder.Services.AddSingleton<CollectionImportEngine>();
builder.Services.AddSingleton<ICollectionImportEngine>(sp => sp.GetRequiredService<CollectionImportEngine>());
builder.Services.AddHostedService<AttemptReconciliationService>();
builder.Services.AddSingleton<ImportCoordinator>();
var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.UseStaticFiles();
app.MapStaticAssets();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

static bool TransientAdoptionError(Exception error) => error switch
{
    Azure.RequestFailedException request => request.Status is 408 or 429 or 500 or 502 or 503 or 504,
    Microsoft.Data.SqlClient.SqlException => true,
    IOException or HttpRequestException or TimeoutException => true,
    _ => error.InnerException is not null && TransientAdoptionError(error.InnerException)
};
