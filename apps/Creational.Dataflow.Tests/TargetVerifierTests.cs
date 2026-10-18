using System.Security.Cryptography;
using Creational.Dataflow.Storage;
using Creational.Dataflow.Targets;
using Xunit;

namespace Creational.Dataflow.Tests;

public class TargetVerifierTests
{
    [Fact]
    public async Task CompleteGroupVerifiesAndIgnoresAttemptPrefix()
    {
        var store = new FakeBlobStore();
        store.Add("attempt-1/a", "alpha");
        store.Add("attempt-1/b", "beta");
        store.Add("attempt-2/a", "alpha");
        store.Add("attempt-2/b", "beta");
        var verifier = new TargetVerifier(store);

        var first = await verifier.VerifyAsync(Manifest("attempt-1"), CancellationToken.None);
        var second = await verifier.VerifyAsync(Manifest("attempt-2"), CancellationToken.None);

        Assert.True(first.Valid);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("length")]
    [InlineData("hash")]
    public async Task InvalidMemberRejectsGroup(string fault)
    {
        var store = new FakeBlobStore();
        store.Add("attempt-1/a", "alpha");
        if (fault != "missing") store.Add("attempt-1/b", fault == "length" ? "longer" : "wrong");
        var manifest = Manifest("attempt-1");
        if (fault == "hash") manifest = manifest with
        {
            Artifacts = manifest.Artifacts.Select(a => a.Name == "b" ? a with { Length = 5 } : a).ToArray()
        };

        var result = await new TargetVerifier(store).VerifyAsync(manifest, CancellationToken.None);

        Assert.False(result.Valid);
        Assert.Contains("b", result.Error);
    }

    [Fact]
    public async Task ParseFinalizerFingerprintChangesWithChildFingerprint()
    {
        var verifier = new TargetVerifier(new FakeBlobStore());
        var first = new OutputManifest(new TargetId("collection", "parse-finalize", "all"), "v1", [],
            new Dictionary<string, string> { ["parse-shard/p1"] = "alpha" });
        var second = first with { InputFingerprints = new Dictionary<string, string> { ["parse-shard/p1"] = "beta" } };
        var a = await verifier.VerifyAsync(first, CancellationToken.None);
        var b = await verifier.VerifyAsync(second, CancellationToken.None);
        Assert.True(a.Valid);
        Assert.NotEqual(a.Fingerprint, b.Fingerprint);
    }

    [Fact]
    public async Task PoolReportTimingDoesNotInvalidateParserInput()
    {
        var store = new FakeBlobStore();
        store.Add("one/pool.parquet", "same pool");
        store.Add("one/report.json", "{\"seconds\":1}");
        store.Add("two/pool.parquet", "same pool");
        store.Add("two/report.json", "{\"seconds\":2}");
        OutputManifest Make(string prefix, string report) => new(
            new TargetId("c", "candidate-pool-shard", "p1"), "v1",
            [new("pool.parquet", $"{prefix}/pool.parquet", 9, Hash("same pool"), "parquet"),
             new("report.json", $"{prefix}/report.json", report.Length, Hash(report), "json")],
            new Dictionary<string, string>());
        var verifier = new TargetVerifier(store);
        var a = await verifier.VerifyAsync(Make("one", "{\"seconds\":1}"), CancellationToken.None);
        var b = await verifier.VerifyAsync(Make("two", "{\"seconds\":2}"), CancellationToken.None);
        Assert.True(a.Valid);
        Assert.Equal(a.Fingerprint, b.Fingerprint);
    }

    [Fact]
    public async Task EquivalentAliasMapsHaveSameFingerprint()
    {
        var store = new FakeBlobStore();
        var aText = "{\"fingerprint\":\"old\",\"aliases\":{\"speciesbox\":\"Speciesbox\"}}";
        var bText = "{\"aliases\":{\"speciesbox\":\"Speciesbox\"}}";
        store.Add("one/aliases.json", aText);
        store.Add("two/aliases.json", bText);
        OutputManifest Make(string prefix, string value) => new(
            new TargetId("c", "alias-seed", "all"), "v1",
            [new("aliases.json", $"{prefix}/aliases.json", value.Length, Hash(value), "json")],
            new Dictionary<string, string>());
        var verifier = new TargetVerifier(store);
        var a = await verifier.VerifyAsync(Make("one", aText), CancellationToken.None);
        var b = await verifier.VerifyAsync(Make("two", bText), CancellationToken.None);
        Assert.True(a.Valid);
        Assert.Equal(a.Fingerprint, b.Fingerprint);
    }

    private static OutputManifest Manifest(string prefix) => new(
        new TargetId("collection", "parse-shard", "p1"), "v1",
        [
            new ArtifactDescriptor("a", $"{prefix}/a", 5, Hash("alpha"), "text"),
            new ArtifactDescriptor("b", $"{prefix}/b", 4, Hash("beta"), "text")
        ], new Dictionary<string, string>());

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class FakeBlobStore : IBlobStore
    {
        private readonly Dictionary<string, byte[]> _items = [];
        public void Add(string path, string text) => _items[path] = System.Text.Encoding.UTF8.GetBytes(text);
        public Task<BlobInfo?> HeadAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(_items.TryGetValue(path, out var data) ? new BlobInfo(data.Length, Hash(System.Text.Encoding.UTF8.GetString(data))) : null);
        public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(_items[path]));
    }
}
