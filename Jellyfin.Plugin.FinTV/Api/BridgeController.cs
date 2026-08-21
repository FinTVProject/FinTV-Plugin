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

    public BridgeController(ITaskManager taskManager, Services.FinTvServerClient client)
    {
        _taskManager = taskManager;
        _client = client;
    }

    [HttpPost("test-connection")]
    public async Task<ActionResult<Services.ConnectionTestResult>> TestConnection(
        [FromBody] ConnectionTestRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await _client.TestConnectionAsync(request?.ServerUrl, request?.ApiKey, cancellationToken);
        return Ok(result);
    }

    [HttpPost("sync")]
    public IActionResult SyncNow()
    {
        _taskManager.CancelIfRunningAndQueue<Services.CatalogSyncTask>();
        return Accepted();
    }

    [HttpPost("blackframe")]
    public IActionResult Blackframe()
    {
        _taskManager.CancelIfRunningAndQueue<Services.BlackframeChapterTask>();
        return Accepted();
    }
}

public sealed class ConnectionTestRequest
{
    public string? ServerUrl { get; set; }

    public string? ApiKey { get; set; }
}
