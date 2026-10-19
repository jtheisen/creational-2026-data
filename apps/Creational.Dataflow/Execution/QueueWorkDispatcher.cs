using System.Text.Json;
using Azure.Storage.Queues;
using Creational.Dataflow.Storage;
using Creational.Dataflow.Targets;

namespace Creational.Dataflow.Execution;

public sealed record QueueWorkItem(TargetId Target, string AttemptId, long StaleRevision,
    string InputManifestUri, string OutputPrefix, string RecipeVersion);

public sealed class QueueWorkDispatcher(QueueClient queue, ITargetRepository repository,
    IComputeJobLauncher launcher, ILogger<QueueWorkDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var batch = await queue.ReceiveMessagesAsync(maxMessages: 5,
                    visibilityTimeout: TimeSpan.FromMinutes(5), cancellationToken: stoppingToken);
                if (batch.Value.Length == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }
                foreach (var message in batch.Value)
                {
                    try
                    {
                        var work = JsonSerializer.Deserialize<QueueWorkItem>(message.Body.ToString())
                            ?? throw new InvalidDataException("Empty queue work item");
                        await ProcessAsync(work, repository, launcher, stoppingToken);
                        await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, stoppingToken);
                    }
                    catch (Exception error)
                    {
                        logger.LogError(error, "Could not dispatch queue message {MessageId}", message.MessageId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogError(error, "Queue dispatcher poll failed");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    public static async Task<bool> ProcessAsync(QueueWorkItem item, ITargetRepository repository,
        IComputeJobLauncher launcher, CancellationToken ct)
    {
        var states = await repository.GetCollectionAsync(item.Target.Collection, ct);
        if (!states.TryGetValue(item.Target, out var current) || current.AttemptId != item.AttemptId ||
            current.Outcome != "queued") return false;
        if (current.AttemptRevision != item.StaleRevision || current.InputManifestUri != item.InputManifestUri ||
            item.OutputPrefix != CollectionImportEngine.AttemptOutputPrefix(item.Target, item.AttemptId))
            throw new InvalidDataException($"Queue message does not match persisted attempt {item.AttemptId}");
        try { await repository.BeginLaunchAsync(item.Target, item.AttemptId, ct); }
        catch (InvalidOperationException) { return false; } // Another dispatcher won the ETag race.

        // Once the launch is claimed, an HTTP failure can mean Azure accepted the start.
        // Never start it again from a redelivered message without resolving that ambiguity.
        var execution = await launcher.StartAsync(item.Target, item.InputManifestUri,
            item.OutputPrefix, item.RecipeVersion, ct);
        await repository.AttachExecutionAsync(item.Target, item.AttemptId, execution, ct);
        return true;
    }
}
