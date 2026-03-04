using System.IO.Pipes;
using System.Text;

namespace EstherLink.Ipc;

public sealed class NamedPipeJsonServer
{
    private readonly string _pipeName;
    private readonly Func<IpcRequest, CancellationToken, Task<IpcResponse>> _handler;

    public NamedPipeJsonServer(string pipeName, Func<IpcRequest, CancellationToken, Task<IpcResponse>> handler)
    {
        _pipeName = pipeName;
        _handler = handler;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            await server.WaitForConnectionAsync(cancellationToken);
            await HandleConnectionAsync(server, cancellationToken);
        }
    }

    private async Task HandleConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 1024, leaveOpen: true);

        var requestLine = await reader.ReadLineAsync().WaitAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(requestLine))
        {
            return;
        }

        var request = IpcJson.Deserialize<IpcRequest>(requestLine);
        if (request is null)
        {
            var invalidResponse = IpcJson.Serialize(new IpcResponse(false, "Invalid request payload."));
            await writer.WriteLineAsync(invalidResponse);
            return;
        }

        var response = await _handler(request, cancellationToken);
        await writer.WriteLineAsync(IpcJson.Serialize(response));
    }
}
