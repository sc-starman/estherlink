using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using OmniRelay.Core.Networking;

namespace OmniRelay.Service.Runtime;

public sealed class Socks5ProxyEngine
{
    private static readonly IPAddress CloudflareDnsIpv4 = IPAddress.Parse("1.1.1.1");
    private static readonly IPAddress GoogleDnsIpv4 = IPAddress.Parse("8.8.8.8");

    private readonly GatewayRuntime _runtime;
    private readonly ILogger<Socks5ProxyEngine> _logger;
    private readonly FileLogWriter _fileLog;
    private readonly object _sync = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _listenerCts;
    private Task? _acceptLoop;
    private int _activePort;

    public Socks5ProxyEngine(
        GatewayRuntime runtime,
        ILogger<Socks5ProxyEngine> logger,
        FileLogWriter fileLog)
    {
        _runtime = runtime;
        _logger = logger;
        _fileLog = fileLog;
    }

    public Task<int> EnsureRunningAsync(int port, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_listener is not null && _activePort == port)
            {
                return Task.FromResult(_activePort);
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

    public async Task<int> RestartAsync(int port, CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var (listener, boundPort) = StartListenerWithFallback(port);

        lock (_sync)
        {
            _listener = listener;
            _listenerCts = cts;
            _acceptLoop = AcceptLoopAsync(listener, cts.Token);
            _activePort = boundPort;
        }

        if (boundPort != port)
        {
            PersistLocalProxyPortFallback(port, boundPort);
            _fileLog.Warn($"SOCKS5 proxy listener started on fallback port 127.0.0.1:{boundPort} (requested {port}).");
        }
        else
        {
            _fileLog.Info($"SOCKS5 proxy listener started on 127.0.0.1:{port}.");
        }

        return boundPort;
    }

    private (TcpListener Listener, int BoundPort) StartListenerWithFallback(int requestedPort)
    {
        foreach (var candidatePort in BuildPortCandidates(requestedPort))
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, candidatePort);
                listener.Start();
                return (listener, candidatePort);
            }
            catch (SocketException ex) when (
                ex.SocketErrorCode == SocketError.AccessDenied ||
                ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                if (candidatePort == requestedPort)
                {
                    var diagnostic = BuildBindDiagnostic(requestedPort, ex);
                    _fileLog.Warn(diagnostic);
                    _runtime.SetError(diagnostic);
                }

                continue;
            }
        }

        throw new SocketException((int)SocketError.AccessDenied);
    }

    private static IEnumerable<int> BuildPortCandidates(int requestedPort)
    {
        yield return requestedPort;

        // First pass: nearby candidates to preserve operator intent.
        for (var delta = 1; delta <= 24; delta++)
        {
            var plus = requestedPort + delta;
            if (plus >= 1025 && plus <= 65535)
            {
                yield return plus;
            }

            var minus = requestedPort - delta;
            if (minus >= 1025 && minus <= 65535)
            {
                yield return minus;
            }
        }

        // Second pass: stable high range fallback, usually free from excluded ranges.
        for (var port = 24000; port <= 24100; port++)
        {
            if (port != requestedPort)
            {
                yield return port;
            }
        }
    }

    private static string BuildBindDiagnostic(int port, SocketException ex)
    {
        var endpoints = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        var inUse = endpoints.Any(x => x.Port == port);
        var hint = inUse
            ? "port appears occupied by another process"
            : "port may be reserved/excluded by Windows";
        return $"Failed binding local SOCKS listener to 127.0.0.1:{port} ({ex.SocketErrorCode}/{ex.ErrorCode}): {hint}.";
    }

    private void PersistLocalProxyPortFallback(int requestedPort, int fallbackPort)
    {
        try
        {
            var config = _runtime.GetConfigSnapshot();
            if (config.LocalProxyListenPort == fallbackPort)
            {
                return;
            }

            if (config.LocalProxyListenPort != requestedPort)
            {
                return;
            }

            config.LocalProxyListenPort = fallbackPort;
            _runtime.SetConfig(config);
            _runtime.SetError($"Local proxy port {requestedPort} was unavailable; switched to {fallbackPort}.");
        }
        catch (Exception ex)
        {
            _fileLog.Warn($"Unable to persist fallback local proxy port {fallbackPort}: {ex.Message}");
        }
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
            catch (SocketException ex) when (
                ex.SocketErrorCode == SocketError.ConnectionReset ||
                ex.SocketErrorCode == SocketError.OperationAborted ||
                ex.SocketErrorCode == SocketError.Interrupted)
            {
                continue;
            }
            catch (Exception ex)
            {
                _runtime.SetError(ex.Message);
                _fileLog.Error("SOCKS5 proxy accept loop failure.", ex);
                _logger.LogError(ex, "SOCKS5 proxy accept loop failure.");
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectionCts.CancelAfter(TimeSpan.FromMinutes(10));
        var stream = client.GetStream();
        var config = _runtime.GetConfigSnapshot();
        var sourceAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;

        try
        {
            await NegotiateAsync(stream, connectionCts.Token);
            var target = await ReadConnectRequestAsync(stream, connectionCts.Token);
            var destinationIp = await ResolveDestinationIpv4Async(target.Host, connectionCts.Token);

            if (_runtime.ShouldBlockDestination(destinationIp))
            {
                await SendReplyAsync(stream, 0x02, IPAddress.Any, 0, connectionCts.Token);
                _fileLog.Info(
                    $"SOCKS5 blocked source={sourceAddress} target={target.Host}:{target.Port} ip={destinationIp} blacklistMatch=true");
                return;
            }

            var shouldUseWhitelist = _runtime.ShouldUseWhitelistAdapter(destinationIp);
            var adapterIndex = shouldUseWhitelist ? config.WhitelistAdapterIfIndex : config.DefaultAdapterIfIndex;
            if (!NetworkAdapterCatalog.TryGetPrimaryIpv4(adapterIndex, out var bindIp) || bindIp is null)
            {
                await SendReplyAsync(stream, 0x01, IPAddress.Any, 0, connectionCts.Token);
                _runtime.SetError($"Adapter IfIndex {adapterIndex} has no usable IPv4 address.");
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
            await SendReplyAsync(
                stream,
                0x00,
                localEndpoint?.Address ?? IPAddress.Any,
                localEndpoint?.Port ?? 0,
                connectionCts.Token);

            if (!IsInternalProbeTraffic(sourceAddress, target, destinationIp))
            {
                _fileLog.Info(
                    $"SOCKS5 CONNECT source={sourceAddress} target={target.Host}:{target.Port} ip={destinationIp} " +
                    $"egressIfIndex={adapterIndex} bindIp={bindIp} whitelistMatch={shouldUseWhitelist}");
            }

            using var outboundStream = new NetworkStream(outboundSocket, ownsSocket: true);
            await RelayAsync(stream, outboundStream, connectionCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ex is IOException ioEx &&
                ioEx.Message.Contains("Unexpected EOF", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _runtime.SetError(ex.Message);
            _fileLog.Error("SOCKS5 proxy connection handling failed.", ex);

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
                throw new InvalidOperationException("IPv6 is not supported.");
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

    private static bool IsInternalProbeTraffic(IPAddress? sourceAddress, (string Host, int Port) target, IPAddress destinationIp)
    {
        if (sourceAddress is null || !IPAddress.IsLoopback(sourceAddress) || target.Port != 443)
        {
            return false;
        }

        if (target.Host.Equals("1.1.1.1", StringComparison.OrdinalIgnoreCase) ||
            target.Host.Equals("8.8.8.8", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return destinationIp.Equals(CloudflareDnsIpv4) || destinationIp.Equals(GoogleDnsIpv4);
    }
}
