using System.IO.Pipes;
using System.Text;

namespace OmniRelay.Ipc;

public sealed class NamedPipeJsonClient
{
    private readonly string _pipeName;

    public NamedPipeJsonClient(string pipeName)
    {
        _pipeName = pipeName;
    }

    public async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken cancellationToken = default)
    {
        using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000, cancellationToken);

        using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 1024, leaveOpen: true);

        var requestJson = IpcJson.Serialize(request);
        await writer.WriteLineAsync(requestJson);

        var responseJson = await reader.ReadLineAsync().WaitAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return new IpcResponse(false, "No response from service.");
        }

        return IpcJson.Deserialize<IpcResponse>(responseJson) ?? new IpcResponse(false, "Invalid response payload.");
    }
}
