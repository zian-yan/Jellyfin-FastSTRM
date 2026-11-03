using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FastSTRM.Api;

/// <summary>
/// API endpoints for FastSTRM probe management. Admin-only.
/// </summary>
[ApiController]
[Route("FastStrm")]
[Authorize(Policy = "RequiresElevation")]
public class FastStrmController : ControllerBase
{
    private readonly ProbeService _probeService;

    public FastStrmController(ProbeService probeService)
    {
        _probeService = probeService;
    }

    /// <summary>
    /// Returns a summary of .strm items in the library.
    /// </summary>
    /// <param name="refresh">Bypasses the 30s cache, e.g. right after a probe finished.</param>
    [HttpGet("Summary")]
    public ActionResult GetSummary([FromQuery] bool refresh = false)
    {
        var (total, probed, unprobed) = _probeService.GetSummary(refresh);
        var config = Plugin.Instance?.Configuration;
        return Ok(new
        {
            Total = total,
            Probed = probed,
            Unprobed = unprobed,
            SkipProbeForUnprobedStrm = config?.SkipProbeForUnprobedStrm ?? true,
            EnableBackgroundProbeForUnprobedStrm = config?.EnableBackgroundProbeForUnprobedStrm ?? false,
            BackgroundProbeDelaySeconds = config?.GetBackgroundProbeDelaySeconds() ?? 5,
            CancelBackgroundProbeOnPlaybackStop = config?.CancelBackgroundProbeOnPlaybackStop ?? true
        });
    }

    /// <summary>
    /// Starts a probe task. Mode: "Incremental" or "Full".
    /// </summary>
    [HttpPost("Probe/Start")]
    public ActionResult StartProbe([FromQuery] string mode = ProbeService.IncrementalMode)
    {
        // Reject unknown modes instead of letting them fall through to a full-library rescan.
        if (!ProbeService.TryParseMode(mode, out var normalizedMode))
        {
            return BadRequest(new
            {
                Message = $"Unsupported mode '{mode}'. Use '{ProbeService.IncrementalMode}' or '{ProbeService.FullMode}'."
            });
        }

        if (_probeService.StartProbe(normalizedMode))
        {
            return NoContent();
        }

        return Conflict(new { Message = "A probe task is already running or still stopping." });
    }

    /// <summary>
    /// Stops the currently running probe task.
    /// </summary>
    [HttpPost("Probe/Stop")]
    public ActionResult StopProbe()
    {
        _probeService.StopProbe();
        return NoContent();
    }

    /// <summary>
    /// Returns the current probe task status and progress.
    /// </summary>
    [HttpGet("Probe/Status")]
    public ActionResult GetProbeStatus()
    {
        return Ok(new
        {
            _probeService.State,
            Processed = _probeService.ProcessedItems,
            Total = _probeService.TotalItems,
            CurrentItem = _probeService.CurrentItemName
        });
    }
}
