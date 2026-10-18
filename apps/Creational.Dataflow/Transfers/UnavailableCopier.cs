namespace Creational.Dataflow.Transfers;

public sealed class UnavailableCopier(string reason) : IStreamingCopier
{
    public Task<CopyResult> CopyAsync(CopyTarget target, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(reason);
}
