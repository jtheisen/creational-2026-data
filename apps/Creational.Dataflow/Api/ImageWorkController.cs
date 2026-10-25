using Creational.Dataflow.Images;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace Creational.Dataflow.Api;

[ApiController]
[Route("api/v1/images")]
public sealed class ImageWorkController(IServiceProvider services) : ControllerBase
{
    private SqlImageWorkRepository? Work => services.GetService<SqlImageWorkRepository>();

    [HttpGet("work")]
    public async Task<IActionResult> GetWork(CancellationToken ct)
    {
        var work = Work;
        if (work is null) return NotFound();
        var settings = await Task.WhenAll(
            work.GetSettingAsync(ImageWorkKind.Metadata, ct),
            work.GetSettingAsync(ImageWorkKind.Copy, ct),
            work.GetSettingAsync(ImageWorkKind.Resize, ct));
        return Ok(new
        {
            settings = settings.Select(setting => new
            {
                kind = setting.Kind.ToString(), setting.Enabled, setting.MinimumPriority,
                effectiveMinimumPriority = Math.Max(1, setting.MinimumPriority),
                setting.Concurrency, setting.PacingMilliseconds
            }),
            queue = (await work.QueueCountsAsync(ct)).Select(row => new
            {
                kind = row.Kind.ToString(), row.State, row.Count
            }),
            resizeBatches = await work.ListOpenResizeBatchesAsync(ct)
        });
    }

    [HttpPut("work/settings/{kind}")]
    public async Task<IActionResult> SetWork(string kind, [FromBody] ImageWorkSettingRequest request,
        CancellationToken ct)
    {
        var work = Work;
        if (work is null) return NotFound();
        if (!Enum.TryParse<ImageWorkKind>(kind, true, out var parsed) ||
            parsed is < ImageWorkKind.Metadata or > ImageWorkKind.Resize)
            return BadRequest("Unknown image work kind.");
        if (!float.IsFinite(request.MinimumPriority) || request.Concurrency is < 1 or > 16 ||
            request.PacingMilliseconds is < 0 or > 60_000)
            return BadRequest("Invalid work setting.");
        var setting = new ImageWorkSetting(parsed, request.Enabled, request.MinimumPriority,
            request.Concurrency, request.PacingMilliseconds);
        await work.SetSettingAsync(setting, ct);
        return Ok(new
        {
            kind = parsed.ToString(), setting.Enabled, setting.MinimumPriority,
            effectiveMinimumPriority = Math.Max(1, setting.MinimumPriority),
            setting.Concurrency, setting.PacingMilliseconds
        });
    }

    [HttpPut("{imageId:int}/priority")]
    public async Task<IActionResult> SetImagePriority(int imageId, [FromBody] PriorityRequest request,
        CancellationToken ct)
    {
        var work = Work;
        if (work is null) return NotFound();
        if (imageId < 1 || !float.IsFinite(request.Value)) return BadRequest();
        try { await work.SetImagePriorityAsync(imageId, request.Value, ct); }
        catch (SqlException error) when (error.Number == 51010) { return NotFound(); }
        return Ok(new { imageId, priority = request.Value });
    }

    [HttpPut("work/ott/{ottId:int}/priority")]
    public async Task<IActionResult> SetSubtreePriority(int ottId, [FromBody] PriorityRequest request,
        CancellationToken ct)
    {
        var work = Work;
        if (work is null) return NotFound();
        if (ottId < 1 || !float.IsFinite(request.Value)) return BadRequest();
        try
        {
            var count = await work.SetSubtreePriorityAsync(ottId, request.Value, ct);
            return Ok(new { ottId, priority = request.Value, images = count });
        }
        catch (SqlException error) when (error.Number == 51009) { return NotFound(); }
    }

    [HttpPost("{imageId:int}/metadata/stale")]
    public async Task<IActionResult> MarkMetadataStale(int imageId, CancellationToken ct)
    {
        var work = Work;
        if (work is null) return NotFound();
        if (imageId < 1) return BadRequest();
        try { await work.MarkMetadataStaleAsync(imageId, ct); }
        catch (SqlException error) when (error.Number == 51010) { return NotFound(); }
        return Ok(new { imageId, metadataStale = true });
    }

    [HttpPost("{imageId:int}/source/stale")]
    public async Task<IActionResult> MarkSourceStale(int imageId, CancellationToken ct)
    {
        var work = Work;
        if (work is null) return NotFound();
        if (imageId < 1) return BadRequest();
        try { await work.MarkSourceStaleAsync(imageId, ct); }
        catch (SqlException error) when (error.Number == 51010) { return NotFound(); }
        catch (SqlException error) when (error.Number == 51011) { return Conflict(error.Message); }
        catch (SqlException error) when (error.Number == 51012) { return Conflict(error.Message); }
        return Ok(new { imageId, sourceStale = true });
    }
}

public sealed record PriorityRequest(float Value);
public sealed record ImageWorkSettingRequest(bool Enabled, float MinimumPriority,
    byte Concurrency, int PacingMilliseconds);
