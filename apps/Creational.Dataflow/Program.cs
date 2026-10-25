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
builder.Services.AddSingleton<ITargetRepository, TableTargetRepository>();
builder.Services.AddSingleton<ICollectionSettingsRepository, TableCollectionSettingsRepository>();
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
builder.Services.AddSingleton<ResizeSweepService>();
builder.Services.AddSingleton<IResizeSweepScheduler>(sp => sp.GetRequiredService<ResizeSweepService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ResizeSweepService>());
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
    sp.GetRequiredService<IResizeSweepScheduler>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<ThumbnailImportService>());
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
