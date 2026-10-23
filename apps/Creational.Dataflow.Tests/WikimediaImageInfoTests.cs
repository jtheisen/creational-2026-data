using System.Net;
using System.Net.Http;
using Creational.Dataflow.Images;
using Xunit;

namespace Creational.Dataflow.Tests;

public class WikimediaImageInfoTests
{
    [Fact]
    public async Task DistinctFilenameCaseGetsDistinctMetadata()
    {
        var handler = new StubHandler(_ => """
            {"query":{"pages":{"1":{"title":"File:Bird.jpg","imageinfo":[{"size":100}]},
                               "2":{"title":"File:Bird.JPG","imageinfo":[{"size":200}]}}}}
            """);
        using var http = new HttpClient(handler);
        var lookup = new WikimediaImageInfo(http, "CreationalDataflow/1.0 (contact: test@example.org)");

        var result = await lookup.ResolveAsync(["File:Bird.jpg", "File:Bird.JPG"], CancellationToken.None);

        Assert.Equal(100, result["File:Bird.jpg"].OriginalBytes);
        Assert.Equal(200, result["File:Bird.JPG"].OriginalBytes);
    }

    [Fact]
    public async Task PrefersLocalWikipediaAndFallsBackToCommons()
    {
        var handler = new StubHandler(request => request.RequestUri!.Host switch
        {
            "commons.wikimedia.org" => """
                {"query":{"pages":{"1":{"title":"File:Commons.jpg","imageinfo":[{"thumburl":"https://upload.wikimedia.org/thumb.jpg","descriptionurl":"https://commons.wikimedia.org/wiki/File:Commons.jpg","url":"https://upload.wikimedia.org/full.jpg","size":123,"extmetadata":{"LicenseShortName":{"value":"CC BY-SA 4.0"},"LicenseUrl":{"value":"https://creativecommons.org/licenses/by-sa/4.0/"},"Artist":{"value":"<b>Jane</b> Doe"}}}]},"3":{"title":"File:Both.jpg","imageinfo":[{"descriptionurl":"https://commons.wikimedia.org/wiki/File:Both.jpg","size":999}]},"-1":{"title":"File:Local.jpg","missing":true}}}}
                """,
            "en.wikipedia.org" => """
                {"query":{"pages":{"2":{"title":"File:Local.jpg","imageinfo":[{"thumburl":"https://upload.wikimedia.org/local-thumb.jpg","descriptionurl":"https://en.wikipedia.org/wiki/File:Local.jpg","url":"https://upload.wikimedia.org/local.jpg","size":456,"extmetadata":{}}]},"4":{"title":"File:Both.jpg","imageinfo":[{"descriptionurl":"https://en.wikipedia.org/wiki/File:Both.jpg","size":100}]}}}}
                """,
            _ => throw new InvalidOperationException("Unexpected host")
        });
        using var http = new HttpClient(handler);
        var lookup = new WikimediaImageInfo(http, "CreationalDataflow/1.0 (contact: test@example.org)");

        var result = await lookup.ResolveAsync(["File:Commons.jpg", "File:Local.jpg", "File:Both.jpg"], CancellationToken.None);

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal("CC BY-SA 4.0", result["File:Commons.jpg"].License);
        Assert.Equal("Jane Doe", result["File:Commons.jpg"].Attribution);
        Assert.False(result["File:Commons.jpg"].NeedsRightsReview);
        Assert.Equal("https://en.wikipedia.org/wiki/File:Local.jpg", result["File:Local.jpg"].DescriptionUrl);
        Assert.True(result["File:Local.jpg"].NeedsRightsReview);
        Assert.Equal(100, result["File:Both.jpg"].OriginalBytes);
        await lookup.ResolveAsync(["File:Commons.jpg"], CancellationToken.None);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task DifferentWidthsUseDifferentMetadataRequests()
    {
        var widths = new List<string>();
        var handler = new StubHandler(request =>
        {
            widths.Add(request.RequestUri!.Query);
            return """{"query":{"pages":{"1":{"title":"File:Bird.jpg","imageinfo":[{"thumburl":"https://upload.wikimedia.org/bird.jpg"}]}}}}""";
        });
        using var http = new HttpClient(handler);
        var lookup = new WikimediaImageInfo(http, "CreationalDataflow/1.0 (contact: test@example.org)");

        await lookup.ResolveAsync(["File:Bird.jpg"], 250, CancellationToken.None);
        await lookup.ResolveAsync(["File:Bird.jpg"], 320, CancellationToken.None);

        Assert.Equal(2, handler.RequestCount);
        Assert.Contains("iiurlwidth=250", widths[0]);
        Assert.Contains("iiurlwidth=320", widths[1]);
    }

    [Fact]
    public async Task FetchSnapshotsBatchesTitlesWithoutRequestingImageUrls()
    {
        var requests = new List<string>();
        var handler = new StubHandler(request =>
        {
            requests.Add(request.RequestUri!.Query);
            return """
                {"query":{"pages":{"1":{"title":"File:A.jpg","imageinfo":[{"size":100,"sha1":"abc","timestamp":"2026-10-01T00:00:00Z","extmetadata":{"LicenseShortName":{"value":"CC BY 4.0"},"LicenseUrl":{"value":"https://creativecommons.org/licenses/by/4.0/"},"Artist":{"value":"<b>Alex</b>"},"UsageTerms":{"value":"Credit Alex"}}}]},"2":{"title":"File:B.jpg","imageinfo":[{"size":200,"extmetadata":{}}]}}}}
                """;
        });
        using var http = new HttpClient(handler);
        var lookup = new WikimediaImageInfo(http, "CreationalDataflow/1.0 (contact: test@example.org)");

        var snapshots = await lookup.FetchSnapshotsAsync(["File:A.jpg", "File:B.jpg"], "commonswiki", CancellationToken.None);

        Assert.Single(requests);
        Assert.Contains("titles=File%3AA.jpg%7CFile%3AB.jpg", requests[0]);
        Assert.DoesNotContain("iiprop=url", requests[0]);
        Assert.DoesNotContain("iiurlwidth", requests[0]);
        Assert.Contains("UsageTerms", snapshots["File:A.jpg"].RawJson);
        Assert.Equal("CC BY 4.0", snapshots["File:A.jpg"].Metadata.License);
        Assert.Equal("Alex", snapshots["File:A.jpg"].Metadata.Attribution);
    }

    [Fact]
    public void ThumbnailRedirectUsesSourceWikiAndRequestedWidth()
    {
        Assert.Equal(
            "https://commons.wikimedia.org/w/index.php?title=Special%3ARedirect%2Ffile%2FBird.jpg&width=250",
            WikimediaImageInfo.ThumbnailRedirect("commonswiki", "File:Bird.jpg", 250));
        Assert.Equal(
            "https://en.wikipedia.org/w/index.php?title=Special%3ARedirect%2Ffile%2FBird.jpg&width=330",
            WikimediaImageInfo.ThumbnailRedirect("enwiki", "File:Bird.jpg", 330));
    }

    [Fact]
    public async Task FetchSnapshotFollowsFileTitleRedirect()
    {
        var handler = new StubHandler(_ => """
            {"query":{"redirects":[{"from":"File:Old.jpg","to":"File:New.jpg"}],
            "pages":{"1":{"title":"File:New.jpg","imageinfo":[{"size":42}]}}}}
            """);
        using var http = new HttpClient(handler);
        var lookup = new WikimediaImageInfo(http, "CreationalDataflow/1.0 (contact: test@example.org)");

        var snapshots = await lookup.FetchSnapshotsAsync(["File:Old.jpg"], "commonswiki", CancellationToken.None);

        Assert.Equal("complete", snapshots["File:Old.jpg"].Status);
        Assert.Equal("File:New.jpg", snapshots["File:Old.jpg"].Metadata.FileTitle);
    }

    [Fact]
    public async Task NonImageMediaIsNotOfferedAsAThumbnail()
    {
        var handler = new StubHandler(_ => """
            {"query":{"pages":{"1":{"title":"File:Birdsong.ogg","imageinfo":[{"size":42,"mediatype":"AUDIO"}]}}}}
            """);
        using var http = new HttpClient(handler);
        var lookup = new WikimediaImageInfo(http, "CreationalDataflow/1.0 (contact: test@example.org)");

        var snapshots = await lookup.FetchSnapshotsAsync(["File:Birdsong.ogg"], "commonswiki", CancellationToken.None);

        Assert.Equal("unsupported", snapshots["File:Birdsong.ogg"].Status);
        Assert.Null(snapshots["File:Birdsong.ogg"].Metadata.ThumbnailUrl);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, string> response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response(request))
            });
        }
    }
}
