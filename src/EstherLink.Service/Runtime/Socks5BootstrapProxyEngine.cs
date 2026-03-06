using System.Net;
using System.Net.Sockets;
using EstherLink.Core.Networking;

namespace EstherLink.Service.Runtime;

public sealed class Socks5BootstrapProxyEngine
{
    private readonly GatewayRuntime _runtime;
    private readonly ILogger<Socks5BootstrapProxyEngine> _logger;
    private readonly FileLogWriter _fileLog;
    private readonly object _sync = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _listenerCts;
    private Task? _acceptLoop;
    private int _activePort;

    public Socks5BootstrapProxyEngine(
        GatewayRuntime runtime,
        ILogger<Socks5BootstrapProxyEngine> logger,
        FileLogWriter fileLog)
    {
        _runtime = runtime;
        _logger = logger;
        _fileLog = fileLog;
    }

    public Task EnsureRunningAsync(int port, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_listener is not null && _activePort == port)
            {
                return Task.CompletedTask;
            }
        }

        return RestartAsync(port, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? pendingLoop;
        CancellationTokenSource? cts;

        lock (_sync)
        {
            pendingLoop = _acceptLoop;
            cts = _listenerCts;
            _listener?.Stop();
            _listener = null;
            _listenerCts = null;
            _acceptLoop = null;
            _activePort = 0;
        }

        cts?.Cancel();
        if (pendingLoop is not null)
        {
            try
            {
                await pendingLoop.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        cts?.Dispose();
    }

    public async Task RestartAsync(int port, CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        lock (_sync)
        {
            _listener = listener;
            _listenerCts = cts;
            _acceptLoop = AcceptLoopAsync(listener, cts.Token);
            _activePort = port;
        }

        _fileLog.Info($"Bootstrap SOCKS5 listener started on 127.0.0.1:{port}.");
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
                _runtime.SetError(ex.Message);
                _fileLog.Error("SOCKS5 accept loop failure.", ex);
                _logger.LogError(ex, "SOCKS5 accept loop failure.");
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectionCts.CancelAfter(TimeSpan.FromMinutes(10));
        var stream = client.GetStream();
        var config = _runtime.GetConfigSnapshot();

        try
        {
            await NegotiateAsync(stream, connectionCts.Token);
            var target = await ReadConnectRequestAsync(stream, connectionCts.Token);
            var destinationIp = await ResolveDestinationIpv4Async(target.Host, connectionCts.Token);

            if (!NetworkAdapterCatalog.TryGetPrimaryIpv4(config.DefaultAdapterIfIndex, out var bindIp) || bindIp is null)
            {
                await SendReplyAsync(stream, 0x01, IPAddress.Any, 0, connectionCts.Token);
                _runtime.SetBootstrapSocksStatus(true, _runtime.GetStatusSnapshot().TunnelConnected, $"Default adapter IfIndex {config.DefaultAdapterIfIndex} has no usable IPv4 address.");
                return;
            }

            using var outboundSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            outboundSocket.Bind(new IPEndPoint(bindIp, 0));
            await outboundSocket.ConnectAsync(new IPEndPoint(destinationIp, target.Port), connectionCts.Token);
            var localEndpoint = (IPEndPoint?)outboundSocket.LocalEndPoint;
            await SendReplyAsync(stream, 0x00, localEndpoint?.Address ?? IPAddress.Any, localEndpoint?.Port ?? 0, connectionCts.Token);

            using var outboundStream = new NetworkStream(outboundSocket, ownsSocket: true);
            _fileLog.Info($"SOCKS5 CONNECT target={target.Host}:{target.Port} ip={destinationIp} bindIp={bindIp} ifIndex={config.DefaultAdapterIfIndex}");
            await RelayAsync(stream, outboundStream, connectionCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _fileLog.Error("SOCKS5 connection handling failed.", ex);
            _runtime.SetBootstrapSocksStatus(true, _runtime.GetStatusSnapshot().TunnelConnected, ex.Message);
            try
            {
                await SendReplyAsync(stream, 0x01, IPAddress.Any, 0, CancellationToken.None);
            }
            catch
            {
            }
        }
        finally
        {
            client.Close();
        }
    }

    private static async Task NegotiateAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = await ReadExactAsync(stream, 2, cancellationToken);
        if (header[0] != 0x05)
        {
            throw new InvalidOperationException("Invalid SOCKS version.");
        }

        var methods = await ReadExactAsync(stream, header[1], cancellationToken);
        var supportsNoAuth = methods.Contains((byte)0x00);
        if (!supportsNoAuth)
        {
            await stream.WriteAsync(new byte[] { 0x05, 0xFF }, cancellationToken);
            throw new InvalidOperationException("SOCKS client has no supported auth method.");
        }

        await stream.WriteAsync(new byte[] { 0x05, 0x00 }, cancellationToken);
    }

    private static async Task<(string Host, int Port)> ReadConnectRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = await ReadExactAsync(stream, 4, cancellationToken);
        if (header[0] != 0x05 || header[1] != 0x01)
        {
            throw new InvalidOperationException("Only SOCKS5 CONNECT is supported.");
        }

        if (header[2] != 0x00)
        {
            throw new InvalidOperationException("Invalid SOCKS reserved byte.");
        }

        string host;
        switch (header[3])
        {
            case 0x01:
                host = new IPAddress(await ReadExactAsync(stream, 4, cancellationToken)).ToString();
                break;
            case 0x03:
                var len = (await ReadExactAsync(stream, 1, cancellationToken))[0];
                host = System.Text.Encoding.ASCII.GetString(await ReadExactAsync(stream, len, cancellationToken));
                break;
            case 0x04:
                throw new InvalidOperationException("IPv6 is not supported for bootstrap SOCKS.");
            default:
                throw new InvalidOperationException("Unsupported SOCKS address type.");
        }

        var portBytes = await ReadExactAsync(stream, 2, cancellationToken);
        var port = (portBytes[0] << 8) | portBytes[1];
        if (port <= 0 || port > 65535)
        {
            throw new InvalidOperationException("Invalid SOCKS destination port.");
        }

        return (host, port);
    }

    private static async Task SendReplyAsync(Stream stream, byte replyCode, IPAddress bindIp, int bindPort, CancellationToken cancellationToken)
    {
        var addressBytes = bindIp.AddressFamily == AddressFamily.InterNetwork ? bindIp.GetAddressBytes() : IPAddress.Any.GetAddressBytes();
        var portBytes = new[] { (byte)((bindPort >> 8) & 0xFF), (byte)(bindPort & 0xFF) };
        var response = new byte[10];
        response[0] = 0x05;
        response[1] = replyCode;
        response[2] = 0x00;
        response[3] = 0x01;
        Buffer.BlockCopy(addressBytes, 0, response, 4, 4);
        Buffer.BlockCopy(portBytes, 0, response, 8, 2);
        await stream.WriteAsync(response, cancellationToken);
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

    private static async Task RelayAsync(Stream clientStream, Stream outboundStream, CancellationToken cancellationToken)
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

    private static async Task<IPAddress> ResolveDestinationIpv4Async(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            return parsed;
        }

        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        var ipv4 = addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork);
        if (ipv4 is null)
        {
            throw new InvalidOperationException($"Could not resolve IPv4 address for '{host}'.");
        }

        return ipv4;
    }
}
