namespace Creational.Dataflow.Targets;

public sealed record TargetId(string Collection, string Kind, string Key)
{
    public override string ToString() => $"{Collection}/{Kind}/{Key}";
}

public sealed record ManifestShard(string Key, string FileName, long Bytes, string Sha256);
