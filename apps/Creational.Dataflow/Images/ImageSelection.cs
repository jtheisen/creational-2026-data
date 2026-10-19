namespace Creational.Dataflow.Images;

public enum ImageSelectionKind { OttBranch, WikipediaPage }

public sealed record ImageSelection(ImageSelectionKind Kind, string Value)
{
    public static ImageSelection Parse(string input)
    {
        var text = input.Trim();
        if (long.TryParse(text, out var ottId) && ottId > 0)
            return new(ImageSelectionKind.OttBranch, ottId.ToString());

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps &&
            uri.Host.Equals("en.wikipedia.org", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.StartsWith("/wiki/", StringComparison.Ordinal))
        {
            var title = Uri.UnescapeDataString(uri.AbsolutePath[6..]).Replace('_', ' ').Trim();
            if (title.Length > 0 && !title.Contains(':') && !title.Contains('/'))
                return new(ImageSelectionKind.WikipediaPage, title);
        }

        throw new ArgumentException("Enter a positive OTT ID or an English Wikipedia article URL.", nameof(input));
    }
}
