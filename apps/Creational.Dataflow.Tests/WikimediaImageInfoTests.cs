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
