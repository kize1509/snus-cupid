public sealed class CupidBackgroundService : BackgroundService
{
    private readonly CupidRegistry _registry;
    private readonly ILogger<CupidBackgroundService> _logger;

    public CupidBackgroundService(CupidRegistry registry, ILogger<CupidBackgroundService> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var result = await _registry.SendLettersAsync(stoppingToken);
                _logger.LogInformation(
                    "Cupid tick delivered {DeliveredLetters}/{RegisteredPeople} letters.",
                    result.LettersDelivered,
                    result.RegisteredPeople);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown path.
        }
    }
}
