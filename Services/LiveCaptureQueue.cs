using System.Threading.Channels;

namespace EntregasApi.Services;

public interface ILiveCaptureQueue
{
    ValueTask QueueAsync(int liveSessionId, CancellationToken cancellationToken = default);
    ValueTask<int> DequeueAsync(CancellationToken cancellationToken);
}

public class LiveCaptureQueue : ILiveCaptureQueue
{
    private readonly Channel<int> _queue = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    public ValueTask QueueAsync(int liveSessionId, CancellationToken cancellationToken = default)
    {
        return _queue.Writer.WriteAsync(liveSessionId, cancellationToken);
    }

    public ValueTask<int> DequeueAsync(CancellationToken cancellationToken)
    {
        return _queue.Reader.ReadAsync(cancellationToken);
    }
}

public class LiveCaptureBackgroundService : BackgroundService
{
    private readonly ILiveCaptureQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LiveCaptureBackgroundService> _logger;

    public LiveCaptureBackgroundService(
        ILiveCaptureQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<LiveCaptureBackgroundService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var liveSessionId = await _queue.DequeueAsync(stoppingToken);

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<ILiveCaptureService>();
                await service.ProcessAsync(liveSessionId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fallo el procesamiento del live {LiveSessionId}", liveSessionId);
            }
        }
    }
}
