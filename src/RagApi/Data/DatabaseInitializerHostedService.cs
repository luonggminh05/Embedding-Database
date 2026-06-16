namespace RagApi.Data;

public sealed class DatabaseInitializerHostedService : IHostedService
{
    private readonly ISqlDocumentRepository _repository;
    private readonly ILogger<DatabaseInitializerHostedService> _logger;

    public DatabaseInitializerHostedService(ISqlDocumentRepository repository, ILogger<DatabaseInitializerHostedService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Initializing SQL Server schema.");
        await _repository.InitializeAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
