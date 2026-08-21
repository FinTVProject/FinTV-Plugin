using MediaBrowser.Common.Api;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FinTV.Api;

[ApiController]
[Route("FinTV/api/bridge")]
[Authorize(Policy = Policies.RequiresElevation)]
public class BridgeController : ControllerBase
{
    private readonly ITaskManager _taskManager;
    private readonly Services.FinTvServerClient _client;
    private readonly Services.LiveTvRegistrar _liveTv;
    private readonly Services.CatalogSyncTask _catalogSync;

    public BridgeController(
        ITaskManager taskManager,
        Services.FinTvServerClient client,
        Services.LiveTvRegistrar liveTv,
        Services.CatalogSyncTask catalogSync)
    {
        _taskManager = taskManager;
        _client = client;
        _liveTv = liveTv;
        _catalogSync = catalogSync;
    }

    [HttpPost("test-connection")]
    public async Task<ActionResult<Services.ConnectionTestResult>> TestConnection(
        [FromBody] ConnectionTestRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await _client.TestConnectionAsync(request?.ServerUrl, request?.ApiKey, cancellationToken);
        return Ok(result);
    }

    [HttpPost("register-livetv")]
    public async Task<ActionResult<Services.LiveTvRegistrationResult>> RegisterLiveTv(CancellationToken cancellationToken)
    {
        var result = await _liveTv.RegisterAsync(cancellationToken);
        return result.Ok ? Ok(result) : StatusCode(502, result);
    }

    [HttpPost("libraries")]
    public async Task<ActionResult<Services.LibraryPushResult>> PushLibraries(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _catalogSync.PushLibrariesAsync(cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(502, new Services.LibraryPushResult(false, ex.Message));
        }
    }

    [HttpPost("sync")]
    public IActionResult SyncNow()
    {
        return QueueTask("FinTVCatalogSync", "Catalog sync");
    }

    [HttpPost("blackframe")]
    public IActionResult Blackframe()
    {
        return QueueTask("FinTVBlackframe", "Blackframe scan");
    }

    private IActionResult QueueTask(string key, string name)
    {
        var worker = _taskManager.ScheduledTasks.FirstOrDefault(t =>
            string.Equals(t.ScheduledTask.Key, key, StringComparison.Ordinal));
        if (worker is null)
        {
            return StatusCode(500, new
            {
                started = false,
                message = $"{name} is not registered. Restart Jellyfin after installing the plugin."
            });
        }

        if (worker.State != TaskState.Idle)
        {
            return Ok(new { started = true, message = $"{name} is already running." });
        }

        _ = _taskManager.Execute(worker, new TaskOptions());
        return Ok(new { started = true, message = $"{name} started. Watch Dashboard → Scheduled Tasks." });
    }
}

public sealed class ConnectionTestRequest
{
    public string? ServerUrl { get; set; }

    public string? ApiKey { get; set; }
}
