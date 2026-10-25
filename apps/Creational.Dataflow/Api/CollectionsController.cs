using Creational.Dataflow.Execution;
using Creational.Dataflow.Storage;
using Creational.Dataflow.Targets;
using Microsoft.AspNetCore.Mvc;

namespace Creational.Dataflow.Api;

[ApiController]
[Route("api/v1/collections/enwiki-2026-10-01")]
public sealed class CollectionsController(CollectionImportEngine engine, ImportCoordinator coordinator,
    ITargetRepository targets, ICollectionSettingsRepository settings) : ControllerBase
{
    private const string Collection = October2026Collection.Collection;

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var snapshot = await engine.GetRecordedPlanAsync(Collection, ct);
        var parallelism = await settings.GetParallelismAsync(Collection, ct);
        return Ok(new
        {
            collection = Collection,
            parallelism,
            targets = snapshot.Plan.Targets.Select(target => new
            {
                target.Id.Kind, target.Id.Key,
                disposition = target.Disposition.ToString(), target.Reason,
                state = snapshot.States.GetValueOrDefault(target.Id)
            }),
            runs = coordinator.Snapshots().Where(run => run.Collection == Collection)
        });
    }

    [HttpPost("imports")]
    public async Task<IActionResult> Import()
    {
        var runId = await coordinator.StartOrGetAsync(Collection, CancellationToken.None);
        return AcceptedAtAction(nameof(Get), new { runId });
    }

    [HttpPut("settings/parallelism")]
    public async Task<IActionResult> Parallelism([FromBody] ParallelismRequest request, CancellationToken ct)
    {
        if (request.Value is < 1 or > CollectionParallelism.Maximum)
            return BadRequest($"Parallelism must be between 1 and {CollectionParallelism.Maximum}.");
        await settings.SetParallelismAsync(Collection, request.Value, ct);
        return Ok(new { parallelism = request.Value });
    }

    [HttpPost("targets/{kind}/{key}/stale")]
    public async Task<IActionResult> MarkStale(string kind, string key, CancellationToken ct)
    {
        var id = new TargetId(Collection, kind, key);
        var plan = await engine.GetRecordedPlanAsync(Collection, ct);
        if (!plan.Plan.Targets.Any(target => target.Id == id)) return NotFound();
        var state = await targets.MarkStaleAsync(id, "Marked through control API", ct);
        return Ok(state);
    }
}

public sealed record ParallelismRequest(int Value);
