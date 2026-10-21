using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Creational.Dataflow.Images;

public sealed record ImageMetadata(string FileTitle, string? ThumbnailUrl, string? DescriptionUrl,
    string? OriginalUrl, long? OriginalBytes, string? License, string? LicenseUrl,
    string? Attribution, bool NeedsRightsReview, string? Error);

public sealed class WikimediaImageInfo(HttpClient http, string? userAgent)
{
    private readonly ConcurrentDictionary<string, (ImageMetadata Data, DateTimeOffset Until)> _cache =
        new(StringComparer.Ordinal);

    public async Task<IReadOnlyDictionary<string, ImageMetadata>> ResolveAsync(
        IReadOnlyList<string> titles, CancellationToken ct)
    {
        var result = new Dictionary<string, ImageMetadata>(StringComparer.Ordinal);
        var missing = titles.Distinct(StringComparer.Ordinal).Where(title =>
        {
            if (_cache.TryGetValue(title, out var cached) && cached.Until > DateTimeOffset.UtcNow)
            {
                result[title] = cached.Data;
                return false;
            }
            return true;
        }).ToArray();
        if (missing.Length == 0) return result;
        if (string.IsNullOrWhiteSpace(userAgent))
            throw new InvalidOperationException("Set Copies:UserAgent for Wikimedia metadata requests.");

        foreach (var batch in missing.Chunk(10))
        {
            var local = await FetchAsync("en.wikipedia.org", batch, ct);
            var remaining = batch.Where(title => !local.ContainsKey(Normalize(title))).ToArray();
            var commons = remaining.Length == 0
                ? new Dictionary<string, ImageMetadata>(StringComparer.Ordinal)
                : await FetchAsync("commons.wikimedia.org", remaining, ct);
            foreach (var title in batch)
            {
                var key = Normalize(title);
                var data = local.GetValueOrDefault(key) ?? commons.GetValueOrDefault(key) ??
                    new ImageMetadata(title, null, null, null, null, null, null, null, true,
                        "File was not found on Commons or English Wikipedia, or metadata was unavailable.");
                result[title] = data;
                _cache[title] = (data, DateTimeOffset.UtcNow.AddHours(12));
            }
        }
        return result;
    }

    private async Task<Dictionary<string, ImageMetadata>> FetchAsync(string host,
        IReadOnlyList<string> titles, CancellationToken ct)
    {
        var result = new Dictionary<string, ImageMetadata>(StringComparer.Ordinal);
        var url = $"https://{host}/w/api.php?action=query&format=json&prop=imageinfo" +
            "&iiprop=url%7Csize%7Cextmetadata&iiurlwidth=320" +
            "&iiextmetadatafilter=LicenseShortName%7CLicenseUrl%7CArtist%7CCredit" +
            "&titles=" + Uri.EscapeDataString(string.Join('|', titles));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(userAgent);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!json.RootElement.TryGetProperty("query", out var query) ||
            !query.TryGetProperty("pages", out var pages)) return result;
        foreach (var page in pages.EnumerateObject())
        {
            var body = page.Value;
            if (!body.TryGetProperty("title", out var titleElement) ||
                !body.TryGetProperty("imageinfo", out var infos) || infos.GetArrayLength() == 0)
                continue;
            var title = titleElement.GetString()!;
            var info = infos[0];
            var ext = info.TryGetProperty("extmetadata", out var metadata) ? metadata : default;
            var license = Plain(Ext(ext, "LicenseShortName"));
            var licenseUrl = SafeHttps(Ext(ext, "LicenseUrl"));
            var attribution = Plain(Ext(ext, "Artist")) ?? Plain(Ext(ext, "Credit"));
            result[Normalize(title)] = new(title, SafeHttps(Value(info, "thumburl")),
                SafeHttps(Value(info, "descriptionurl")), SafeHttps(Value(info, "url")),
                info.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : null,
                license, licenseUrl, attribution,
                string.IsNullOrWhiteSpace(license) || string.IsNullOrWhiteSpace(licenseUrl), null);
        }
        return result;
    }

    private static string Normalize(string title)
    {
        title = title.Replace('_', ' ').Trim();
        if (!title.StartsWith("File:", StringComparison.OrdinalIgnoreCase) || title.Length <= 5)
            return title;
        return "File:" + char.ToUpperInvariant(title[5]) + title[6..];
    }

    private static string? Value(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value)
            ? value.GetString() : null;

    private static string? Ext(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var property)
            ? Value(property, "value") : null;

    private static string? Plain(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var withoutTags = Regex.Replace(value, "<[^>]*>", " ");
        var text = Regex.Replace(WebUtility.HtmlDecode(withoutTags), "\\s+", " ").Trim();
        return text.Length > 400 ? text[..400] + "…" : text;
    }

    private static string? SafeHttps(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.ToString() : null;
}
