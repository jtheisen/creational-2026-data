using Creational.Dataflow.Images;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Creational.Dataflow.Tests;

public sealed class ThumbnailImportTests
{
    [Fact]
    public void JobRejectsMoreThanOnePreviewPage()
    {
        var titles = Enumerable.Range(0, 25).Select(x => $"File:Image {x}.jpg").ToArray();
        Assert.Throws<ArgumentException>(() => ThumbnailImportJob.Create(titles, 250));
    }

    [Fact]
    public void RenditionKeyAndPathAreStableAndWidthSpecific()
    {
        var title = "File:Ängström #1.jpg";
        var first = ThumbnailIdentity.For(title, 250);
        Assert.Equal(first, ThumbnailIdentity.For(title, 250));
        Assert.NotEqual(first.RowKey, ThumbnailIdentity.For(title, 330).RowKey);
        Assert.Contains("/250px/", first.BlobName);
        Assert.EndsWith("/Ängström #1.jpg", first.BlobName);
    }

    [Fact]
    public void BatchBoundsAndChildIdsAreStable()
    {
        var selection = ImageSelection.Parse("691846");
        Assert.Throws<ArgumentOutOfRangeException>(() => ThumbnailImportBatch.Create(selection, 0, 1001));
        var batch = ThumbnailImportBatch.Create(selection, 24, 100);
        Assert.Equal(124, batch.EndOffset);
        Assert.Equal(batch.ChildId(24), batch.ChildId(24));
        Assert.NotEqual(batch.ChildId(24), batch.ChildId(48));
    }

    [Fact]
    public void BatchReplayKeepsTheSameChildJob()
    {
        var selection = ImageSelection.Parse("691846");
        var batch = ThumbnailImportBatch.Create(selection, 24, 100);
        var first = ThumbnailImportJob.Create(["File:Cod.jpg"], 250) with
        {
            Id = batch.ChildId(24), BatchId = batch.Id, StartOffset = 24
        };
        var second = ThumbnailImportJob.Create(["File:Cod.jpg"], 250) with
        {
            Id = batch.ChildId(24), BatchId = batch.Id, StartOffset = 24
        };
        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task RestartResumesOnlyUnfinishedRenditions()
    {
        var titles = new[] { "File:Done.jpg", "File:Next.jpg" };
        var job = ThumbnailImportJob.Create(titles, 250);
        var repository = new MemoryRepository();
        await repository.AddJobAsync(job, CancellationToken.None);
        var done = ThumbnailIdentity.For(titles[0], 250);
        await repository.SaveRenditionAsync(new(done.RowKey, titles[0], 250, done.BlobName,
            "complete", 12, "abc"), CancellationToken.None);
        var copier = new FakeCopier();
        using var http = new HttpClient(new MetadataHandler());
        var service = new ThumbnailImportService(repository, copier,
            new WikimediaImageInfo(http, "CreationalDataflow/1.0 (contact: test@example.org)"),
            NullLogger<ThumbnailImportService>.Instance);

        await service.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal([titles[1]], copier.Copied);
        Assert.Equal("complete", (await repository.GetJobAsync(job.Id, CancellationToken.None))!.Status);
        Assert.Equal("complete", (await repository.GetRenditionAsync(titles[1], 250, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task BatchQueuesMoreThanOnePreviewPageAndCanBeFoundLater()
    {
        var titles = Enumerable.Range(0, 25).Select(x => $"File:Image {x}.jpg").ToArray();
        var repository = new MemoryRepository();
        var copier = new FakeCopier();
        var catalog = new FakeCatalog(titles);
        using var http = new HttpClient(new MetadataHandler());
        var service = new ThumbnailImportService(repository, copier,
            new WikimediaImageInfo(http, "CreationalDataflow/1.0 (contact: test@example.org)"),
            NullLogger<ThumbnailImportService>.Instance, catalog);
        var id = await service.CreateBatchAsync(ImageSelection.Parse("691846"), 0, 25, CancellationToken.None);

        await service.ProcessPendingAsync(CancellationToken.None);

        var batch = await service.GetBatchAsync(id, CancellationToken.None);
        Assert.Equal("complete", batch!.Status);
        Assert.Equal(25, batch.NextOffset);
        Assert.Equal(2, (await service.ListBatchJobsAsync(id, CancellationToken.None)).Count);
        Assert.Equal(25, copier.Copied.Count);
        Assert.Equal(1, catalog.QueryCount);
        Assert.Contains(await service.ListBatchesAsync(10, CancellationToken.None), x => x.Id == id);
    }

    [Fact]
    public async Task BatchRestartAdvancesPastExistingChildWithoutDuplicatingIt()
    {
        var titles = Enumerable.Range(0, 25).Select(x => $"File:Image {x}.jpg").ToArray();
        var repository = new MemoryRepository();
        var batch = ThumbnailImportBatch.Create(ImageSelection.Parse("691846"), 0, 25);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        var first = ThumbnailImportJob.Create(titles[..24], 250) with
        {
            Id = batch.ChildId(0), BatchId = batch.Id, StartOffset = 0, Status = "complete"
        };
        await repository.AddJobAsync(first, CancellationToken.None);
        var copier = new FakeCopier();
        using var http = new HttpClient(new MetadataHandler());
        var restarted = new ThumbnailImportService(repository, copier,
            new WikimediaImageInfo(http, "CreationalDataflow/1.0 (contact: test@example.org)"),
            NullLogger<ThumbnailImportService>.Instance, new FakeCatalog(titles));

        await restarted.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(2, repository.JobInsertCount);
        Assert.Equal([titles[24]], copier.Copied);
        Assert.Equal("complete", (await repository.GetBatchAsync(batch.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task ShortCatalogPageFinishesBatchAndKeepsOlderStandaloneJobsVisible()
    {
        var repository = new MemoryRepository();
        var legacy = ThumbnailImportJob.Create(["File:Old.jpg"], 250) with { Status = "complete" };
        await repository.AddJobAsync(legacy, CancellationToken.None);
        var batch = ThumbnailImportBatch.Create(ImageSelection.Parse("691846"), 0, 25);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        using var http = new HttpClient(new MetadataHandler());
        var service = new ThumbnailImportService(repository, new FakeCopier(),
            new WikimediaImageInfo(http, "CreationalDataflow/1.0 (contact: test@example.org)"),
            NullLogger<ThumbnailImportService>.Instance, new FakeCatalog(["File:Only.jpg"]));

        await service.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal("complete", (await service.GetBatchAsync(batch.Id, CancellationToken.None))!.Status);
        Assert.Single(await service.ListBatchJobsAsync(batch.Id, CancellationToken.None));
        Assert.Contains(await service.ListStandaloneJobsAsync(10, CancellationToken.None), x => x.Id == legacy.Id);
    }

    private sealed class MemoryRepository : IThumbnailImportRepository
    {
        private readonly Dictionary<string, ThumbnailImportJob> _jobs = [];
        private readonly Dictionary<string, ThumbnailImportBatch> _batches = [];
        private readonly Dictionary<string, ThumbnailRendition> _renditions = [];
        public int JobInsertCount { get; private set; }
        public Task AddJobAsync(ThumbnailImportJob job, CancellationToken ct)
        { _jobs.Add(job.Id, job); JobInsertCount++; return Task.CompletedTask; }
        public Task<ThumbnailImportJob> AddJobIfMissingAsync(ThumbnailImportJob job, CancellationToken ct)
        { if (!_jobs.ContainsKey(job.Id)) { _jobs.Add(job.Id, job); JobInsertCount++; } return Task.FromResult(_jobs[job.Id]); }
        public Task<ThumbnailImportJob?> GetJobAsync(string id, CancellationToken ct) => Task.FromResult(_jobs.GetValueOrDefault(id));
        public Task<IReadOnlyList<ThumbnailImportJob>> ListJobsAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ThumbnailImportJob>>(_jobs.Values.OrderByDescending(x => x.CreatedAt).Take(limit).ToArray());
        public Task<IReadOnlyList<ThumbnailImportJob>> ListStandaloneJobsAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ThumbnailImportJob>>(_jobs.Values.Where(x => x.BatchId is null)
                .OrderByDescending(x => x.CreatedAt).Take(limit).ToArray());
        public Task<IReadOnlyList<ThumbnailImportJob>> ListBatchJobsAsync(string id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ThumbnailImportJob>>(_jobs.Values.Where(x => x.BatchId == id).OrderBy(x => x.StartOffset).ToArray());
        public Task<IReadOnlyList<ThumbnailImportJob>> ListIncompleteJobsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ThumbnailImportJob>>(_jobs.Values.Where(x => x.Status is "pending" or "running").ToArray());
        public Task SaveJobAsync(ThumbnailImportJob job, CancellationToken ct) { _jobs[job.Id] = job; return Task.CompletedTask; }
        public Task AddBatchAsync(ThumbnailImportBatch batch, CancellationToken ct) { _batches.Add(batch.Id, batch); return Task.CompletedTask; }
        public Task<ThumbnailImportBatch?> GetBatchAsync(string id, CancellationToken ct) => Task.FromResult(_batches.GetValueOrDefault(id));
        public Task<IReadOnlyList<ThumbnailImportBatch>> ListBatchesAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ThumbnailImportBatch>>(_batches.Values.OrderByDescending(x => x.CreatedAt).Take(limit).ToArray());
        public Task<IReadOnlyList<ThumbnailImportBatch>> ListActiveBatchesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ThumbnailImportBatch>>(_batches.Values.Where(x => x.Status is "pending" or "running").ToArray());
        public Task SaveBatchAsync(ThumbnailImportBatch batch, CancellationToken ct) { _batches[batch.Id] = batch; return Task.CompletedTask; }
        public Task<ThumbnailRendition?> GetRenditionAsync(string title, int width, CancellationToken ct) =>
            Task.FromResult(_renditions.GetValueOrDefault(ThumbnailIdentity.For(title, width).RowKey));
        public Task SaveRenditionAsync(ThumbnailRendition row, CancellationToken ct)
        { _renditions[row.RowKey] = row; return Task.CompletedTask; }
        public Task EnsureAssetAsync(string title, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeCatalog(string[] titles) : IImageCatalog
    {
        public int QueryCount { get; private set; }
        public Task<ImageCatalogPage> QueryAsync(ImageSelection selection, int offset, int limit, CancellationToken ct)
        {
            QueryCount++;
            return Task.FromResult(new ImageCatalogPage(1, titles.Length, 0,
                titles.Skip(offset).Take(limit).Select(x => new ImageCandidate(x, "Article", "taxobox")).ToArray()));
        }
    }

    private sealed class FakeCopier : IThumbnailBlobCopier
    {
        public List<string> Copied { get; } = [];
        public Task<bool> IsCurrentAsync(ThumbnailRendition row, CancellationToken ct) =>
            Task.FromResult(row.Status == "complete");
        public Task<ThumbnailCopyResult?> RecoverAsync(ThumbnailRendition row, CancellationToken ct) =>
            Task.FromResult<ThumbnailCopyResult?>(null);
        public Task<ThumbnailCopyResult> CopyAsync(string url, ThumbnailRendition row, CancellationToken ct)
        { Copied.Add(row.Title); return Task.FromResult(new ThumbnailCopyResult(10, "abc")); }
    }

    private sealed class MetadataHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    query = new
                    {
                        pages = Uri.UnescapeDataString(request.RequestUri!.Query.Split('&')
                            .Single(x => x.StartsWith("titles=", StringComparison.Ordinal))[7..])
                            .Split('|').Select((title, index) => new { title, index })
                            .ToDictionary(x => x.index.ToString(), x => new
                            {
                                x.title, imageinfo = new[] { new { thumburl = "https://upload.wikimedia.org/next.jpg" } }
                            })
                    }
                }))
            });
    }
}
