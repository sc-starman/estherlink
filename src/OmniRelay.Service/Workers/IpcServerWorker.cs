using OmniRelay.Ipc;
using OmniRelay.Service.Ipc;

namespace OmniRelay.Service.Workers;

public sealed class IpcServerWorker : BackgroundService
{
    private readonly IpcCommandHandler _handler;
    private readonly Runtime.FileLogWriter _fileLog;
    private readonly ILogger<IpcServerWorker> _logger;

    public IpcServerWorker(
        IpcCommandHandler handler,
        Runtime.FileLogWriter fileLog,
        ILogger<IpcServerWorker> logger)
    {
        _handler = handler;
        _fileLog = fileLog;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting named pipe IPC server on {PipeName}.", PipeNames.Control);
        _fileLog.Info($"IPC server listening on named pipe '{PipeNames.Control}'.");
        var server = new NamedPipeJsonServer(PipeNames.Control, _handler.HandleAsync);
        await server.RunAsync(stoppingToken);
    }
}
