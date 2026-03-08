using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace OmniRelay.Ipc;

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
            try
            {
                using var server = CreateServerStream();

                await server.WaitForConnectionAsync(cancellationToken);
                await HandleConnectionAsync(server, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                // Client disconnected mid-request/response. Keep server alive for next connection.
            }
            catch (ObjectDisposedException)
            {
                // Stream disposed during shutdown or client teardown. Continue serving.
            }
        }
    }

    private NamedPipeServerStream CreateServerStream()
    {
        if (OperatingSystem.IsWindows())
        {
#pragma warning disable CA1416
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));

            return NamedPipeServerStreamAcl.Create(
                _pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                0,
                0,
                security);
#pragma warning restore CA1416
        }

        return new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
    }

    private async Task HandleConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
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
        catch (IOException)
        {
            // Client dropped before response was fully written.
        }
        catch (ObjectDisposedException)
        {
            // Stream was disposed by the peer or runtime teardown.
        }
    }
}
