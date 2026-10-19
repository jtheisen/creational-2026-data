using Creational.Dataflow.Images;
using Xunit;

namespace Creational.Dataflow.Tests;

public class ImageSelectionTests
{
    [Theory]
    [InlineData("114168", ImageSelectionKind.OttBranch, "114168")]
    [InlineData("https://en.wikipedia.org/wiki/Gadus_morhua", ImageSelectionKind.WikipediaPage, "Gadus morhua")]
    [InlineData("https://en.wikipedia.org/wiki/Acer_%28plant%29", ImageSelectionKind.WikipediaPage, "Acer (plant)")]
    public void ParsesSupportedInput(string input, ImageSelectionKind kind, string value)
    {
        var selection = ImageSelection.Parse(input);
        Assert.Equal(kind, selection.Kind);
        Assert.Equal(value, selection.Value);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("https://example.com/wiki/Gadus_morhua")]
    [InlineData("https://en.wikipedia.org/wiki/File:Fish.jpg")]
    [InlineData("Gadus morhua")]
    public void RejectsUnsupportedInput(string input) =>
        Assert.Throws<ArgumentException>(() => ImageSelection.Parse(input));
}
