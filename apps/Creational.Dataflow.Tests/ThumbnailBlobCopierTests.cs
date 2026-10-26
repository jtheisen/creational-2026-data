using System.Net;
using Azure.Storage.Blobs;
using Creational.Dataflow.Images;
using Xunit;

namespace Creational.Dataflow.Tests;

public sealed class ThumbnailBlobCopierTests
{
    [Fact]
    public async Task FailingImage429IsAnImageFailureInsteadOfGlobalThrottle()
    {
        var copier = CreateCopier("Too many thumbnail requests for failing image");
        var rendition = ThumbnailIdentity.For("File:Broken.svg", 250);

        var error = await Assert.ThrowsAsync<ThumbnailPermanentFailureException>(() => copier.CopyAsync(
            "https://thumb.wikimedia.org/wikipedia/commons/thumb/b/b0/Broken.svg/250px-Broken.svg.png",
            new ThumbnailRendition(rendition.RowKey, "File:Broken.svg", 250, rendition.BlobName, "pending"),
            CancellationToken.None));

        Assert.Contains("failing image", error.Message);
    }

    [Fact]
    public async Task OversizedThumbnailIsAPermanentImageFailure()
    {
        var copier = new ThumbnailBlobCopier(new HttpClient(new OversizedHandler()),
            new BlobServiceClient(new Uri("https://unused.blob.core.windows.net")),
            new WikimediaRequestPacer());
        var rendition = ThumbnailIdentity.For("File:Huge.jpg", 250);

        var error = await Assert.ThrowsAsync<ThumbnailPermanentFailureException>(() => copier.CopyAsync(
            "https://thumb.wikimedia.org/wikipedia/commons/thumb/h/h0/Huge.jpg/250px-Huge.jpg",
            new ThumbnailRendition(rendition.RowKey, "File:Huge.jpg", 250, rendition.BlobName, "pending"),
            CancellationToken.None));

        Assert.Contains("16 MiB", error.Message);
    }

    [Fact]
    public async Task Ordinary429StillThrottlesTheImport()
    {
        var copier = CreateCopier("Rate limit exceeded");
        var rendition = ThumbnailIdentity.For("File:Busy.jpg", 250);

        await Assert.ThrowsAsync<WikimediaThrottledException>(() => copier.CopyAsync(
            "https://thumb.wikimedia.org/wikipedia/commons/thumb/b/b0/Busy.jpg/250px-Busy.jpg",
            new ThumbnailRendition(rendition.RowKey, "File:Busy.jpg", 250, rendition.BlobName, "pending"),
            CancellationToken.None));
    }

    private static ThumbnailBlobCopier CreateCopier(string body) => new(
        new HttpClient(new Handler(body)),
        new BlobServiceClient(new Uri("https://unused.blob.core.windows.net")),
        new WikimediaRequestPacer());

    private sealed class Handler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            { Content = new StringContent(body) });
    }

    private sealed class OversizedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([])
            };
            response.Content.Headers.ContentLength = 16 * 1024 * 1024 + 1;
            return Task.FromResult(response);
        }
    }
}
