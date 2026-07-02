using System.Threading.Channels;
using Microsoft.Extensions.Options;
using RagApi.Options;

namespace RagApi.Services.Ingestion;

public class DocumentIngestionWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IngestionOptions _options;
    private readonly ILogger<DocumentIngestionWorker> _logger;
    private readonly Channel<string> _fileQueue;
    private FileSystemWatcher? _watcher;
    private readonly HashSet<string> _processedFingerprints = new();
    private readonly Dictionary<string, int> _retryCounts = new();

    public DocumentIngestionWorker(
        IServiceProvider serviceProvider,
        IOptions<IngestionOptions> options,
        ILogger<DocumentIngestionWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _logger = logger;
        
        // Unbounded channel for queueing files
        _fileQueue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _options.Validate();

        if (!_options.Enabled)
        {
            _logger.LogInformation("Ingestion is disabled. DocumentIngestionWorker will not start.");
            return;
        }

        var directory = _options.PapersDirectory;
        if (!Path.IsPathRooted(directory))
        {
            directory = Path.Combine(Directory.GetCurrentDirectory(), directory);
        }

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            _logger.LogInformation("Created papers directory at {Directory}", directory);
        }

        _logger.LogInformation("Watching directory for new files: {Directory}", directory);

        // Process existing files on startup
        foreach (var file in Directory.GetFiles(directory))
        {
            _fileQueue.Writer.TryWrite(file);
        }

        // Setup FileSystemWatcher
        _watcher = new FileSystemWatcher(directory)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };

        _watcher.Created += (sender, e) => OnFileEvent(e.FullPath);
        _watcher.Changed += (sender, e) => OnFileEvent(e.FullPath);

        // Process the queue
        await ProcessQueueAsync(stoppingToken);
    }

    private void OnFileEvent(string filePath)
    {
        if (File.Exists(filePath))
        {
            _fileQueue.Writer.TryWrite(filePath);
        }
    }

    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
    {
        var lastEnqueued = new Dictionary<string, DateTime>();

        await foreach (var filePath in _fileQueue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                // Debounce
                if (lastEnqueued.TryGetValue(filePath, out var lastTime))
                {
                    if ((DateTime.UtcNow - lastTime).TotalSeconds < 2)
                    {
                        continue;
                    }
                }
                lastEnqueued[filePath] = DateTime.UtcNow;

                // Wait until file is accessible (not locked by copy)
                if (await WaitForFileAccessAsync(filePath, stoppingToken))
                {
                    var fingerprint = GetFingerprint(filePath);
                    if (_processedFingerprints.Contains(fingerprint))
                    {
                        _logger.LogInformation("Skipping file {FilePath} as it has not changed since last ingestion.", filePath);
                        continue;
                    }

                    using var scope = _serviceProvider.CreateScope();
                    var ingestionService = scope.ServiceProvider.GetRequiredService<DocumentIngestionService>();
                    var success = await ingestionService.ProcessFileAsync(filePath, stoppingToken);

                    if (success)
                    {
                        var finalFingerprint = GetFingerprint(filePath);
                        _processedFingerprints.Add(finalFingerprint);
                        _retryCounts.Remove(filePath);
                    }
                    else
                    {
                        int retries = _retryCounts.GetValueOrDefault(filePath, 0);
                        if (retries < 3)
                        {
                            _retryCounts[filePath] = retries + 1;
                            _logger.LogWarning("Ingestion failed for {FilePath}. Retrying ({RetryCount}/3) in 10 seconds...", filePath, retries + 1);
                            _ = Task.Run(async () => 
                            {
                                try
                                {
                                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                                    _fileQueue.Writer.TryWrite(filePath);
                                }
                                catch (TaskCanceledException) { }
                            }, stoppingToken);
                        }
                        else
                        {
                            _logger.LogError("Ingestion permanently failed for {FilePath} after 3 retries.", filePath);
                            _retryCounts.Remove(filePath);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing file {FilePath} from queue", filePath);
            }
        }
    }

    private string GetFingerprint(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            return $"{filePath}|{fileInfo.LastWriteTimeUtc.Ticks}|{fileInfo.Length}";
        }
        catch
        {
            return filePath;
        }
    }

    internal async Task<bool> WaitForFileAccessAsync(string filePath, CancellationToken stoppingToken)
    {
        int retries = 5;
        int delayMs = 1000;

        long? lastLength = null;
        DateTime? lastWriteTime = null;

        for (int i = 0; i < retries; i++)
        {
            if (stoppingToken.IsCancellationRequested) return false;

            try
            {
                using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var fileInfo = new FileInfo(filePath);
                    long currentLength = fileInfo.Length;
                    DateTime currentWriteTime = fileInfo.LastWriteTimeUtc;

                    if (lastLength.HasValue && lastWriteTime.HasValue)
                    {
                        if (currentLength == lastLength.Value && currentWriteTime == lastWriteTime.Value)
                        {
                            return true;
                        }
                        else
                        {
                            _logger.LogInformation("File {FilePath} metadata changed (Length: {LastLen}->{CurrLen}, Time: {LastTime}->{CurrTime}). Retrying for stability...", 
                                filePath, lastLength.Value, currentLength, lastWriteTime.Value, currentWriteTime);
                        }
                    }

                    lastLength = currentLength;
                    lastWriteTime = currentWriteTime;
                }
            }
            catch (IOException)
            {
                _logger.LogDebug("File {FilePath} is locked, retrying in {Delay}ms...", filePath, delayMs);
            }

            await Task.Delay(delayMs, stoppingToken);
        }
        
        _logger.LogWarning("Failed to access stable file {FilePath} after {Retries} retries", filePath, retries);
        return false;
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        base.Dispose();
    }
}
