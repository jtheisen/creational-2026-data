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
        var claimed = false;
        for (var retry = 0; retry < 5; retry++)
        {
            var states = await repository.GetCollectionAsync(item.Target.Collection, ct);
            var current = states.TryGetValue(item.Target, out var state) ? state : TargetState.Empty(item.Target);
            if (current.AttemptId is not null || current.LastAttemptId == item.AttemptId) return false;
            try
            {
                await repository.BeginAttemptAsync(item.Target, item.AttemptId, current.StaleRevision, ct);
                claimed = true;
                break;
            }
            catch (InvalidOperationException) { }
        }
        if (!claimed) throw new InvalidOperationException($"Could not claim queued target {item.Target} after stale revision races");
        try
        {
            var execution = await launcher.StartAsync(item.Target, item.InputManifestUri,
                item.OutputPrefix, item.RecipeVersion, ct);
            await repository.AttachExecutionAsync(item.Target, item.AttemptId, execution, ct);
            return true;
        }
        catch (Exception error)
        {
            await repository.FailAttemptAsync(item.Target, item.AttemptId, error.Message, ct);
            throw;
        }
    }
}
