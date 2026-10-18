using Creational.Dataflow.Storage;
using Creational.Dataflow.Targets;
using Xunit;

namespace Creational.Dataflow.Tests;

public class RemoteManifestCatalogTests
{
    [Fact]
    public void NormalizedMarkerPathEncodesOneTargetGroup()
    {
        var path = RemoteManifestCatalog.NormalizedMarkerPath(
            new TargetId("enwiki/2026-10-01", "dump-shard", "p10p1113635"));
        Assert.Equal("https://creational.blob.core.windows.net/wikimedia-extracts/enwiki/2026-10-01/target-manifests/adopted/dump-shard/p10p1113635.json", path);
    }
}
