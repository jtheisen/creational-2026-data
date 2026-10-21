using Creational.Dataflow.Targets;
using Creational.Dataflow.Transfers;
using Xunit;

namespace Creational.Dataflow.Tests;

public class ImageDumpPinsTests
{
    [Fact]
    public void PinsDatedOfficialDumps()
    {
        var en = ImageDumpPins.For("enwiki-image-dump");
        var commons = ImageDumpPins.For("commons-image-dump");

        Assert.Equal("https://dumps.wikimedia.org/enwiki/20261001/enwiki-20261001-image.sql.gz", en.SourceUrl);
        Assert.Equal(88247846, en.Length);
        Assert.Equal("b89616490ffcb768821a28334a0cbc4a6569bc37", en.Sha1);
        Assert.Equal("https://dumps.wikimedia.org/commonswiki/20261001/commonswiki-20261001-image.sql.gz", commons.SourceUrl);
        Assert.Equal(18622844065, commons.Length);
        Assert.Equal("cb8ba4891ab33890e87d3ccbc28f32dbdbda91f8", commons.Sha1);
    }

    [Fact]
    public void FirstCopyCreatesVerifiableOutputManifest()
    {
        var id = new TargetId(October2026Collection.Collection, "enwiki-image-dump", "all");
        var inputs = new Dictionary<string, string> { ["source-set"] = "fingerprint" };
        var manifest = ImageDumpPins.OutputManifest(id, "2026-10-01",
            new CopyResult(true, ImageDumpPins.Enwiki.Length, new string('a', 64), null), inputs);

        Assert.Equal(id, manifest.Target);
        Assert.Equal("fingerprint", manifest.InputFingerprints["source-set"]);
        var artifact = Assert.Single(manifest.Artifacts);
        Assert.Equal(ImageDumpPins.Enwiki.BlobUrl, artifact.Path);
        Assert.Equal(ImageDumpPins.Enwiki.FileName, artifact.Name);
        Assert.Equal(new string('a', 64), artifact.Sha256);
        Assert.Equal("gzip-sql", artifact.Format);
    }
}
