namespace Creational.Dataflow.Storage;

public interface ICollectionSettingsRepository
{
    Task<int> GetParallelismAsync(string collection, CancellationToken cancellationToken);
    Task SetParallelismAsync(string collection, int parallelism, CancellationToken cancellationToken);
}
