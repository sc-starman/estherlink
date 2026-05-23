using System.Net;
using System.Net.Sockets;
using OmniRelay.Core.Configuration;
using OmniRelay.Core.Networking;

namespace OmniRelay.Service.Runtime;

public sealed class RelayBootstrapSocksEngine
{
    private readonly RelayConfig _relay;
    private readonly FileLogWriter _log;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public RelayBootstrapSocksEngine(RelayConfig relay, FileLogWriter log)
    {
        _relay = relay;
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
        _listener = new TcpListener(IPAddress.Loopback, _relay.BootstrapSocksLocalPort);
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_listener, _cts.Token);
        _log.Info($"Relay '{_relay.Name}' bootstrap SOCKS listener started on 127.0.0.1:{_relay.BootstrapSocksLocalPort}.");
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
                    _log.Info($"Relay '{_relay.Name}' bootstrap accept loop stopped during controlled shutdown.");
                    break;
                }

                _log.Error($"Relay '{_relay.Name}' bootstrap accept loop failed.", ex);
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
            await RelaySocks5ProxyEngine.NegotiateAsync(stream, connectionCts.Token);
            var target = await RelaySocks5ProxyEngine.ReadConnectRequestAsync(stream, connectionCts.Token);
            var destinationIp = await RelaySocks5ProxyEngine.ResolveDestinationIpv4Async(target.Host, connectionCts.Token);

            if (!NetworkAdapterCatalog.TryGetPrimaryIpv4(_relay.OutgoingAdapterId, _relay.OutgoingAdapterIfIndex, out var bindIp, out _) || bindIp is null)
            {
                await RelaySocks5ProxyEngine.SendReplyAsync(stream, 0x01, IPAddress.Any, 0, connectionCts.Token);
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
            await RelaySocks5ProxyEngine.SendReplyAsync(stream, 0x00, localEndpoint?.Address ?? IPAddress.Any, localEndpoint?.Port ?? 0, connectionCts.Token);
            using var outboundStream = new NetworkStream(outboundSocket, ownsSocket: true);
            await RelaySocks5ProxyEngine.RelayAsync(stream, outboundStream, connectionCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Warn($"Relay '{_relay.Name}' bootstrap SOCKS connection failed: {ex.Message}");
            try { await RelaySocks5ProxyEngine.SendReplyAsync(stream, 0x01, IPAddress.Any, 0, CancellationToken.None); } catch { }
        }
        finally
        {
            client.Close();
        }
    }
}
