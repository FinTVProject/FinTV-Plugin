using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

public sealed class LiveTvRegistrationHostedService : BackgroundService
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2)
    ];

    private readonly LiveTvRegistrar _registrar;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<LiveTvRegistrationHostedService> _logger;

    public LiveTvRegistrationHostedService(
        LiveTvRegistrar registrar,
        IHostApplicationLifetime lifetime,
        ILogger<LiveTvRegistrationHostedService> logger)
    {
        _registrar = registrar;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await WaitForApplicationStarted(stoppingToken).ConfigureAwait(false);

        foreach (var delay in RetryDelays)
        {
            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var result = await _registrar.RegisterAsync(stoppingToken).ConfigureAwait(false);
                if (result.Ok)
                {
                    if (!string.IsNullOrWhiteSpace(result.Message)
                        && !result.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("{Message}", result.Message);
                    }

                    return;
                }

                _logger.LogWarning("FinTV Live TV auto-register: {Message}", result.Message);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not auto-register FinTV Live TV tuner");
            }
        }
    }

    private async Task WaitForApplicationStarted(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var startedReg = _lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        if (_lifetime.ApplicationStarted.IsCancellationRequested)
        {
            return;
        }

        using var stoppingReg = stoppingToken.Register(() => started.TrySetCanceled(stoppingToken));
        await started.Task.ConfigureAwait(false);
    }
}
