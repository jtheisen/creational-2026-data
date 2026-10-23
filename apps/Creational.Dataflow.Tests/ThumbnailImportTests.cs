using Creational.Dataflow.Images;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
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

    private sealed class MemoryRepository : IThumbnailImportRepository
    {
        private readonly Dictionary<string, ThumbnailImportJob> _jobs = [];
        private readonly Dictionary<string, ThumbnailRendition> _renditions = [];
        public Task AddJobAsync(ThumbnailImportJob job, CancellationToken ct) { _jobs.Add(job.Id, job); return Task.CompletedTask; }
        public Task<ThumbnailImportJob?> GetJobAsync(string id, CancellationToken ct) => Task.FromResult(_jobs.GetValueOrDefault(id));
        public Task<IReadOnlyList<ThumbnailImportJob>> ListIncompleteJobsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ThumbnailImportJob>>(_jobs.Values.Where(x => x.Status is "pending" or "running").ToArray());
        public Task SaveJobAsync(ThumbnailImportJob job, CancellationToken ct) { _jobs[job.Id] = job; return Task.CompletedTask; }
        public Task<ThumbnailRendition?> GetRenditionAsync(string title, int width, CancellationToken ct) =>
            Task.FromResult(_renditions.GetValueOrDefault(ThumbnailIdentity.For(title, width).RowKey));
        public Task SaveRenditionAsync(ThumbnailRendition row, CancellationToken ct)
        { _renditions[row.RowKey] = row; return Task.CompletedTask; }
        public Task EnsureAssetAsync(string title, CancellationToken ct) => Task.CompletedTask;
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
                Content = new StringContent("""{"query":{"pages":{"1":{"title":"File:Next.jpg","imageinfo":[{"thumburl":"https://upload.wikimedia.org/next.jpg"}]}}}}""")
            });
    }
}
