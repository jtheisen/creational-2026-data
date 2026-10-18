using System.Security.Cryptography;
using System.Text;
using Creational.Dataflow.Storage;
using Creational.Dataflow.Targets;
using Xunit;

namespace Creational.Dataflow.Tests;

public class SourceManifestLoaderTests
{
    [Fact]
    public async Task ParsesShardKeyAndExpectedHash()
    {
        var checksum = new string('a', 64);
        var store = new MemoryStore($"{checksum}  enwiki-2026-10-01-p10p1113635.xml.bz2\n");
        var shards = await new SourceManifestLoader(store).LoadAsync(CancellationToken.None);
        var shard = Assert.Single(shards);
        Assert.Equal("p10p1113635", shard.Key);
        Assert.Equal(checksum, shard.Sha256);
        Assert.Equal(123, shard.Bytes);
    }

    [Fact]
    public async Task MissingDumpShardStillAllowsGraphToLoad()
    {
        var checksum = new string('a', 64);
        var store = new MemoryStore($"{checksum}  enwiki-2026-10-01-p10p1113635.xml.bz2\n", missingDump: true);
        var shard = Assert.Single(await new SourceManifestLoader(store).LoadAsync(CancellationToken.None));
        Assert.Equal("p10p1113635", shard.Key);
        Assert.Equal(0, shard.Bytes);
    }

    private sealed class MemoryStore(string manifest, bool missingDump = false) : IBlobStore
    {
        public Task<BlobInfo?> HeadAsync(string path, CancellationToken ct) =>
            Task.FromResult<BlobInfo?>(missingDump ? null : new(123, null));
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct) =>
            Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(manifest)));
    }
}
