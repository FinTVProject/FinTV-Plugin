using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ChannelFlow.Api;

[ApiController]
[Route("ChannelFlow/api/bridge")]
[Authorize(Policy = Policies.RequiresElevation)]
public class BridgeController : ControllerBase
{
    private readonly ITaskManager _taskManager;
    private readonly Services.ChannelFlowServerClient _client;
    private readonly Services.LiveTvRegistrar _liveTv;
    private readonly Services.CatalogSyncTask _catalogSync;

    public BridgeController(
        ITaskManager taskManager,
        IHttpClientFactory http,
        ILibraryManager libraryManager,
        IChapterManager chapters,
        ITunerHostManager tunerHosts,
        IListingsManager listings,
        IConfigurationManager config,
        ILoggerFactory loggerFactory)
    {
        _taskManager = taskManager;
        _client = new Services.ChannelFlowServerClient(http);
        _liveTv = new Services.LiveTvRegistrar(
            _client,
            tunerHosts,
            listings,
            config,
            loggerFactory.CreateLogger<Services.LiveTvRegistrar>());
        _catalogSync = new Services.CatalogSyncTask(
            libraryManager,
            chapters,
            http,
            loggerFactory.CreateLogger<Services.CatalogSyncTask>());
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
        return QueueTask("ChannelFlowCatalogSync", "Catalog sync");
    }

    [HttpPost("blackframe")]
    public IActionResult Blackframe()
    {
        return QueueTask("ChannelFlowBlackframe", "Blackframe scan");
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
