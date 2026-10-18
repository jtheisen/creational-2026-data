using System.Text.Json;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Creational.Dataflow.Components;
using Creational.Dataflow.Execution;
using Creational.Dataflow.Storage;
using Creational.Dataflow.Targets;
using Creational.Dataflow.Transfers;

var accountUrl = Environment.GetEnvironmentVariable("CREATIONAL_STORAGE_ACCOUNT_URL")
    ?? "https://creational.blob.core.windows.net";
var credential = new DefaultAzureCredential();
var blobService = new BlobServiceClient(new Uri(accountUrl), credential);
var blobStore = new AzureBlobStore(blobService);

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

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
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
builder.Services.AddSingleton<IOutputManifestReader, AzureOutputManifestReader>();
builder.Services.AddSingleton(sp => new WorkInputBuilder(sp.GetRequiredService<IOutputManifestReader>(),
    webShards, blobStore));
var queueUrl = accountUrl.Replace(".blob.", ".queue.", StringComparison.OrdinalIgnoreCase) + "/collection-work";
var queueClient = new QueueClient(new Uri(queueUrl), credential,
    new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
builder.Services.AddSingleton(queueClient);
builder.Services.AddSingleton<TransferLedger>();
builder.Services.AddSingleton<IBlockBlobWriter, AzureBlockBlobWriter>();
var copyUserAgent = builder.Configuration["Copies:UserAgent"];
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
    builder.Services.AddSingleton<IComputeJobLauncher>(sp => new ContainerAppsJobLauncher(
        new HttpClient(), credential, new ContainerJobOptions(jobResourceId, jobImage,
            builder.Configuration["ContainerJob:ContainerName"] ?? "collection-worker")));
    builder.Services.AddHostedService<QueueWorkDispatcher>();
}
else
    builder.Services.AddSingleton<IComputeJobLauncher>(new UnavailableJobLauncher());
builder.Services.AddSingleton<CollectionImportEngine>();
builder.Services.AddSingleton<ICollectionImportEngine>(sp => sp.GetRequiredService<CollectionImportEngine>());
builder.Services.AddSingleton<ImportCoordinator>();
var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
