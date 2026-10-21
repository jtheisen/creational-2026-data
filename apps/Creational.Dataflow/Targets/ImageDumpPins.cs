namespace Creational.Dataflow.Targets;

using Creational.Dataflow.Transfers;

public sealed record ImageDumpPin(string Kind, string Wiki, string FileName, long Length, string Sha1)
{
    public string SourceUrl => $"https://dumps.wikimedia.org/{Wiki}/20261001/{FileName}";
    public string BlobUrl => $"https://creational.blob.core.windows.net/wikimedia-imports/{Wiki}/2026-10-01/{FileName}";
}

public static class ImageDumpPins
{
    public static readonly ImageDumpPin Enwiki = new("enwiki-image-dump", "enwiki",
        "enwiki-20261001-image.sql.gz", 88247846, "b89616490ffcb768821a28334a0cbc4a6569bc37");

    public static readonly ImageDumpPin Commons = new("commons-image-dump", "commonswiki",
        "commonswiki-20261001-image.sql.gz", 18622844065, "cb8ba4891ab33890e87d3ccbc28f32dbdbda91f8");

    public static ImageDumpPin For(string kind) => kind switch
    {
        "enwiki-image-dump" => Enwiki,
        "commons-image-dump" => Commons,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static OutputManifest OutputManifest(TargetId id, string recipeVersion, CopyResult result,
        IReadOnlyDictionary<string, string> inputs)
    {
        var pin = For(id.Kind);
        if (!result.Success || result.Bytes != pin.Length || result.Sha256.Length != 64)
            throw new InvalidDataException($"Incomplete image dump copy for {id}");
        return new(id, recipeVersion,
            [new ArtifactDescriptor(pin.FileName, pin.BlobUrl, result.Bytes, result.Sha256, "gzip-sql")], inputs);
    }
}
