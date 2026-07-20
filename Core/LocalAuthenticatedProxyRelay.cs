using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VibecodeReg.Core;

internal sealed class LocalAuthenticatedProxyRelay : IDisposable
{
    private readonly ProxyConfiguration _upstream;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoopTask;
    private int _disposed;

    public LocalAuthenticatedProxyRelay(ProxyConfiguration upstream)
    {
        _upstream = upstream;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoopTask = AcceptLoopAsync(_cts.Token);
    }

    public int Port { get; }

    public string LocalProxyServer => $"http://127.0.0.1:{Port}";

    private string ProxyAuthorizationHeader =>
        string.IsNullOrWhiteSpace(_upstream.Username)
            ? string.Empty
            : "Proxy-Authorization: Basic " + Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"{_upstream.Username}:{_upstream.Password}")) + "\r\n";

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();

        try
        {
            _listener.Stop();
        }
        catch
        {
        }

        try
        {
            _acceptLoopTask.GetAwaiter().GetResult();
        }
        catch
        {
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;

            try
            {
                client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                continue;
            }

            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.NoDelay = true;

            try
            {
                var clientStream = client.GetStream();
                var request = await ReadHeaderAsync(clientStream, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                using var upstreamClient = new TcpClient();
                upstreamClient.NoDelay = true;
                await upstreamClient.ConnectAsync(_upstream.Host, _upstream.Port, cancellationToken).ConfigureAwait(false);

                var upstreamStream = upstreamClient.GetStream();
                var requestLine = request.Value.HeaderLines[0];

                if (requestLine.StartsWith("CONNECT ", StringComparison.OrdinalIgnoreCase))
                {
                    var connectRequest = BuildConnectRequest(request.Value.HeaderLines);
                    await WriteAsciiAsync(upstreamStream, connectRequest, cancellationToken).ConfigureAwait(false);

                    if (request.Value.Remainder.Length > 0)
                    {
                        await upstreamStream.WriteAsync(request.Value.Remainder, cancellationToken).ConfigureAwait(false);
                    }

                    var upstreamResponse = await ReadHeaderAsync(upstreamStream, cancellationToken).ConfigureAwait(false);
                    if (upstreamResponse is null)
                    {
                        return;
                    }

                    await clientStream.WriteAsync(upstreamResponse.Value.HeaderBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (upstreamResponse.Value.Remainder.Length > 0)
                    {
                        await clientStream.WriteAsync(upstreamResponse.Value.Remainder, cancellationToken).ConfigureAwait(false);
                    }

                    if (!upstreamResponse.Value.HeaderLines[0].Contains(" 200 ", StringComparison.Ordinal))
                    {
                        return;
                    }

                    await PumpBothWaysAsync(clientStream, upstreamStream, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var forwardedRequest = BuildForwardRequest(request.Value.HeaderLines);
                await WriteAsciiAsync(upstreamStream, forwardedRequest, cancellationToken).ConfigureAwait(false);

                if (request.Value.Remainder.Length > 0)
                {
                    await upstreamStream.WriteAsync(request.Value.Remainder, cancellationToken).ConfigureAwait(false);
                }

                await PumpBothWaysAsync(clientStream, upstreamStream, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private string BuildConnectRequest(IReadOnlyList<string> headerLines)
    {
        var target = headerLines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
        var builder = new StringBuilder();
        builder.Append("CONNECT ").Append(target).Append(" HTTP/1.1\r\n");
        builder.Append("Host: ").Append(target).Append("\r\n");
        builder.Append(ProxyAuthorizationHeader);
        builder.Append("Proxy-Connection: Keep-Alive\r\n");
        builder.Append("Connection: Keep-Alive\r\n\r\n");
        return builder.ToString();
    }

    private string BuildForwardRequest(IReadOnlyList<string> headerLines)
    {
        var builder = new StringBuilder();
        builder.Append(headerLines[0]).Append("\r\n");

        foreach (var line in headerLines)
        {
            if (ReferenceEquals(line, headerLines[0]))
            {
                continue;
            }

            if (line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Proxy-Connection:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            builder.Append(line).Append("\r\n");
        }

        builder.Append(ProxyAuthorizationHeader);
        builder.Append("Connection: close\r\n\r\n");
        return builder.ToString();
    }

    private static async Task WriteAsciiAsync(NetworkStream stream, string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private static async Task PumpBothWaysAsync(NetworkStream clientStream, NetworkStream upstreamStream, CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var clientToUpstream = PumpAsync(clientStream, upstreamStream, linkedCts.Token);
        var upstreamToClient = PumpAsync(upstreamStream, clientStream, linkedCts.Token);

        await Task.WhenAny(clientToUpstream, upstreamToClient).ConfigureAwait(false);
        linkedCts.Cancel();

        try
        {
            await Task.WhenAll(clientToUpstream, upstreamToClient).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static async Task PumpAsync(NetworkStream source, NetworkStream destination, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (bytesRead <= 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<(string HeaderText, byte[] HeaderBytes, string[] HeaderLines, byte[] Remainder)?> ReadHeaderAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            using var memory = new MemoryStream();
            while (true)
            {
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (bytesRead <= 0)
                {
                    return null;
                }

                memory.Write(buffer, 0, bytesRead);
                var data = memory.ToArray();
                var headerEndIndex = IndexOfHeaderTerminator(data);
                if (headerEndIndex < 0)
                {
                    continue;
                }

                var headerBytes = data[..(headerEndIndex + 4)];
                var remainder = data[(headerEndIndex + 4)..];
                var headerText = Encoding.ASCII.GetString(headerBytes[..headerEndIndex]);
                var headerLines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
                return (headerText, headerBytes, headerLines, remainder);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int IndexOfHeaderTerminator(byte[] buffer)
    {
        for (var index = 0; index <= buffer.Length - 4; index++)
        {
            if (buffer[index] == '\r'
                && buffer[index + 1] == '\n'
                && buffer[index + 2] == '\r'
                && buffer[index + 3] == '\n')
            {
                return index;
            }
        }

        return -1;
    }
}
