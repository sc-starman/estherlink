using System.Net;
using System.Net.Sockets;
using OmniRelay.Core.Configuration;
using OmniRelay.Core.Networking;
using OmniRelay.Core.Policy;

namespace OmniRelay.Service.Runtime;

public sealed class RelaySocks5ProxyEngine
{
    private readonly RelayConfig _relay;
    private readonly GatewayRuntime _runtime;
    private readonly FileLogWriter _log;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public RelaySocks5ProxyEngine(
        RelayConfig relay,
        GatewayRuntime runtime,
        FileLogWriter log)
    {
        _relay = relay;
        _runtime = runtime;
        _log = log;
    }

    public bool Running => _listener is not null;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_listener is not null)
        {
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Loopback, _relay.DataPlaneLocalPort);
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_listener, _cts.Token);
        _log.Info($"Relay '{_relay.Name}' data-plane SOCKS listener started on 127.0.0.1:{_relay.DataPlaneLocalPort}.");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var loop = _acceptLoop;
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
        if (loop is not null)
        {
            try { await loop; } catch { }
        }

        _cts?.Dispose();
        _cts = null;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = HandleConnectionAsync(client, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (IsExpectedListenerShutdown(ex, cancellationToken))
                {
                    _log.Info($"Relay '{_relay.Name}' SOCKS accept loop stopped during controlled shutdown.");
                    break;
                }

                _log.Error($"Relay '{_relay.Name}' SOCKS accept loop failed.", ex);
            }
        }
    }

    private static bool IsExpectedListenerShutdown(Exception ex, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return true;
        }

        if (ex is SocketException socketEx)
        {
            return socketEx.SocketErrorCode is SocketError.OperationAborted
                or SocketError.Interrupted
                or SocketError.NotSocket
                or SocketError.InvalidArgument;
        }

        return ex.InnerException is SocketException innerSocketEx &&
               innerSocketEx.SocketErrorCode is SocketError.OperationAborted
                   or SocketError.Interrupted
                   or SocketError.NotSocket
                   or SocketError.InvalidArgument;
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectionCts.CancelAfter(TimeSpan.FromMinutes(10));
        var stream = client.GetStream();
        try
        {
            await NegotiateAsync(stream, connectionCts.Token);
            var target = await ReadConnectRequestAsync(stream, connectionCts.Token);
            var destinationIp = await ResolveDestinationIpv4Async(target.Host, connectionCts.Token);

            var policyMatch = _runtime.EvaluateRelayPolicyDestination(_relay.Id, destinationIp);
            if (policyMatch.Action == PolicyMatchAction.Blacklist)
            {
                await SendReplyAsync(stream, 0x02, IPAddress.Any, 0, connectionCts.Token);
                return;
            }

            var useIncoming = policyMatch.Action == PolicyMatchAction.Whitelist;
            var adapterId = useIncoming ? _relay.IncomingAdapterId : _relay.OutgoingAdapterId;
            var fallbackIfIndex = useIncoming ? _relay.IncomingAdapterIfIndex : _relay.OutgoingAdapterIfIndex;
            if (!NetworkAdapterCatalog.TryGetPrimaryIpv4(adapterId, fallbackIfIndex, out var bindIp, out _) || bindIp is null)
            {
                await SendReplyAsync(stream, 0x01, IPAddress.Any, 0, connectionCts.Token);
                return;
            }

            using var outboundSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            outboundSocket.Bind(new IPEndPoint(bindIp, 0));
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(connectionCts.Token))
            {
                connectCts.CancelAfter(TimeSpan.FromSeconds(10));
                await outboundSocket.ConnectAsync(new IPEndPoint(destinationIp, target.Port), connectCts.Token);
            }

            var localEndpoint = (IPEndPoint?)outboundSocket.LocalEndPoint;
            await SendReplyAsync(stream, 0x00, localEndpoint?.Address ?? IPAddress.Any, localEndpoint?.Port ?? 0, connectionCts.Token);
            using var outboundStream = new NetworkStream(outboundSocket, ownsSocket: true);
            await RelayAsync(stream, outboundStream, connectionCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Warn($"Relay '{_relay.Name}' SOCKS connection failed: {ex.Message}");
            try { await SendReplyAsync(stream, 0x01, IPAddress.Any, 0, CancellationToken.None); } catch { }
        }
        finally
        {
            client.Close();
        }
    }

    internal static async Task NegotiateAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = await ReadExactAsync(stream, 2, cancellationToken);
        if (header[0] != 0x05)
        {
            throw new InvalidOperationException("Invalid SOCKS version.");
        }

        var methods = await ReadExactAsync(stream, header[1], cancellationToken);
        if (!methods.Contains((byte)0x00))
        {
            await stream.WriteAsync(new byte[] { 0x05, 0xFF }, cancellationToken);
            throw new InvalidOperationException("SOCKS client has no supported auth method.");
        }

        await stream.WriteAsync(new byte[] { 0x05, 0x00 }, cancellationToken);
    }

    internal static async Task<(string Host, int Port)> ReadConnectRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = await ReadExactAsync(stream, 4, cancellationToken);
        if (header[0] != 0x05 || header[1] != 0x01 || header[2] != 0x00)
        {
            throw new InvalidOperationException("Invalid SOCKS CONNECT request.");
        }

        var host = header[3] switch
        {
            0x01 => new IPAddress(await ReadExactAsync(stream, 4, cancellationToken)).ToString(),
            0x03 => await ReadDomainAsync(stream, cancellationToken),
            _ => throw new InvalidOperationException("Unsupported SOCKS address type.")
        };
        var portBytes = await ReadExactAsync(stream, 2, cancellationToken);
        return (host, (portBytes[0] << 8) | portBytes[1]);
    }

    internal static async Task SendReplyAsync(Stream stream, byte replyCode, IPAddress bindIp, int bindPort, CancellationToken cancellationToken)
    {
        var addressBytes = bindIp.AddressFamily == AddressFamily.InterNetwork ? bindIp.GetAddressBytes() : IPAddress.Any.GetAddressBytes();
        var response = new byte[10];
        response[0] = 0x05;
        response[1] = replyCode;
        response[2] = 0x00;
        response[3] = 0x01;
        Buffer.BlockCopy(addressBytes, 0, response, 4, 4);
        response[8] = (byte)((bindPort >> 8) & 0xFF);
        response[9] = (byte)(bindPort & 0xFF);
        await stream.WriteAsync(response, cancellationToken);
    }

    internal static async Task<IPAddress> ResolveDestinationIpv4Async(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            return parsed;
        }

        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        return addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)
               ?? throw new InvalidOperationException($"Could not resolve IPv4 address for '{host}'.");
    }

    internal static async Task RelayAsync(Stream clientStream, Stream outboundStream, CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var a = PumpAsync(clientStream, outboundStream, linkedCts.Token);
        var b = PumpAsync(outboundStream, clientStream, linkedCts.Token);
        await Task.WhenAny(a, b);
        linkedCts.Cancel();
        try { await a; } catch { }
        try { await b; } catch { }
    }

    private static async Task PumpAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[32 * 1024];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read <= 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            await destination.FlushAsync(cancellationToken);
        }
    }

    private static async Task<string> ReadDomainAsync(Stream stream, CancellationToken cancellationToken)
    {
        var length = (await ReadExactAsync(stream, 1, cancellationToken))[0];
        return System.Text.Encoding.ASCII.GetString(await ReadExactAsync(stream, length, cancellationToken));
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken);
            if (read == 0)
            {
                throw new IOException("Unexpected EOF.");
            }

            offset += read;
        }

        return buffer;
    }
}
