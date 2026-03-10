using System.Net;
using System.Net.Sockets;
using System.Text;
using OmniRelay.Core.Networking;

namespace OmniRelay.Service.Runtime;

public sealed class HttpConnectProxyEngine
{
    private readonly GatewayRuntime _runtime;
    private readonly ILogger<HttpConnectProxyEngine> _logger;
    private readonly FileLogWriter _fileLog;
    private readonly object _sync = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _listenerCts;
    private Task? _acceptLoop;
    private int _activePort;

    public HttpConnectProxyEngine(
        GatewayRuntime runtime,
        ILogger<HttpConnectProxyEngine> logger,
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

    private async Task RestartAsync(int port, CancellationToken cancellationToken)
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

        _fileLog.Info($"Proxy listener started on 127.0.0.1:{port}.");
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
                _fileLog.Error("Proxy accept loop failure.", ex);
                _logger.LogError(ex, "Proxy accept loop failure.");
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectionCts.CancelAfter(TimeSpan.FromMinutes(10));

        var config = _runtime.GetConfigSnapshot();
        var stream = client.GetStream();
        IPAddress? sourceAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;

        try
        {
            var request = await ReadConnectRequestAsync(stream, connectionCts.Token);
            var destinationIp = await ResolveDestinationIpv4Async(request.Host, connectionCts.Token);
            var shouldBlock = _runtime.ShouldBlockDestination(destinationIp);
            if (shouldBlock)
            {
                await SendHttpErrorAsync(stream, 403, "Forbidden");
                _fileLog.Info(
                    $"CONNECT blocked source={sourceAddress} target={request.Host}:{request.Port} ip={destinationIp} blacklistMatch=true");
                return;
            }

            var shouldUseWhitelist = _runtime.ShouldUseWhitelistAdapter(destinationIp);
            var adapterIndex = shouldUseWhitelist ? config.WhitelistAdapterIfIndex : config.DefaultAdapterIfIndex;

            if (!NetworkAdapterCatalog.TryGetPrimaryIpv4(adapterIndex, out var bindIp) || bindIp is null)
            {
                await SendHttpErrorAsync(stream, 502, "Adapter not available");
                _runtime.SetError($"Adapter IfIndex {adapterIndex} has no usable IPv4 address.");
                return;
            }

            using var outboundSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            outboundSocket.Bind(new IPEndPoint(bindIp, 0));
            await outboundSocket.ConnectAsync(new IPEndPoint(destinationIp, request.Port), connectionCts.Token);

            using var outboundStream = new NetworkStream(outboundSocket, ownsSocket: true);
            await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), connectionCts.Token);

            _fileLog.Info(
                $"CONNECT source={sourceAddress} target={request.Host}:{request.Port} ip={destinationIp} " +
                $"egressIfIndex={adapterIndex} bindIp={bindIp} whitelistMatch={shouldUseWhitelist}");

            await RelayAsync(stream, outboundStream, connectionCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _runtime.SetError(ex.Message);
            _fileLog.Error("Proxy connection handling failed.", ex);

            try
            {
                await SendHttpErrorAsync(stream, 502, "Bad Gateway");
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

    private static async Task RelayAsync(Stream clientStream, Stream outboundStream, CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pumpClientToServer = PumpAsync(clientStream, outboundStream, linkedCts.Token);
        var pumpServerToClient = PumpAsync(outboundStream, clientStream, linkedCts.Token);

        await Task.WhenAny(pumpClientToServer, pumpServerToClient);
        linkedCts.Cancel();

        try { await pumpClientToServer; } catch { }
        try { await pumpServerToClient; } catch { }
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

    private static async Task SendHttpErrorAsync(Stream stream, int code, string message)
    {
        var bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {message}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private static async Task<ConnectRequest> ReadConnectRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(1024);
        var one = new byte[1];

        while (bytes.Count < 16 * 1024)
        {
            var read = await stream.ReadAsync(one, cancellationToken);
            if (read == 0)
            {
                throw new InvalidOperationException("Client closed before sending request.");
            }

            bytes.Add(one[0]);
            if (bytes.Count >= 4 &&
                bytes[^4] == (byte)'\r' &&
                bytes[^3] == (byte)'\n' &&
                bytes[^2] == (byte)'\r' &&
                bytes[^1] == (byte)'\n')
            {
                break;
            }
        }

        var requestText = Encoding.ASCII.GetString(bytes.ToArray());
        var lines = requestText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            throw new InvalidOperationException("Invalid HTTP request.");
        }

        var parts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only HTTP CONNECT is supported.");
        }

        if (!TryParseAuthority(parts[1], out var host, out var port))
        {
            throw new InvalidOperationException("CONNECT target is invalid.");
        }

        return new ConnectRequest(host, port);
    }

    private static bool TryParseAuthority(string authority, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        var value = (authority ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.StartsWith('['))
        {
            var closeIndex = value.IndexOf(']');
            if (closeIndex <= 0 || closeIndex + 2 > value.Length || value[closeIndex + 1] != ':')
            {
                return false;
            }

            host = value[1..closeIndex];
            return int.TryParse(value[(closeIndex + 2)..], out port) && port > 0 && port <= 65535;
        }

        var separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
        {
            return false;
        }

        host = value[..separator];
        return int.TryParse(value[(separator + 1)..], out port) && port > 0 && port <= 65535;
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

    private sealed record ConnectRequest(string Host, int Port);
}
