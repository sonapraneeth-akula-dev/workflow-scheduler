using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Npgsql;

namespace Wf.Integration.Tests.Recovery;

internal sealed class PostgresFaultProxy : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentDictionary<int, (TcpClient Client, TcpClient Server)> connections = new();
    private readonly ConcurrentBag<Task> workers = [];
    private readonly NpgsqlConnectionStringBuilder upstream;
    private readonly Task accepting;
    private int sequence;
    private int dropCommit;
    private int dropped;
    private volatile bool available = true;
    public int DroppedCommitCount => Volatile.Read(ref dropped);
    public string ConnectionString { get; }

    public PostgresFaultProxy(string connectionString)
    {
        upstream = new(connectionString);
        listener.Start();
        ConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            SslMode = SslMode.Disable, Pooling = false, Timeout = 1, CommandTimeout = 3
        }.ConnectionString;
        accepting = AcceptAsync();
    }

    public void DropNextCommitAcknowledgement() => Interlocked.Exchange(ref dropCommit, 1);
    public void SetAvailable(bool value)
    {
        available = value;
        if (!value)
            foreach (var connection in connections.Values)
            {
                connection.Client.Dispose();
                connection.Server.Dispose();
            }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync(stopping.Token);
                if (!available) { client.Dispose(); continue; }
                workers.Add(ForwardAsync(client));
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    private async Task ForwardAsync(TcpClient client)
    {
        using (client)
        using (var server = new TcpClient())
        using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token))
        {
            var id = Interlocked.Increment(ref sequence);
            try
            {
                await server.ConnectAsync(upstream.Host ?? throw new InvalidOperationException("Proxy upstream host is required."),
                    upstream.Port, cancellation.Token);
                connections[id] = (client, server);
                var sending = client.GetStream().CopyToAsync(server.GetStream(), cancellation.Token);
                var receiving = ServerMessagesAsync(server.GetStream(), client.GetStream(), cancellation.Token);
                await Task.WhenAny(sending, receiving);
                await cancellation.CancelAsync();
                client.Dispose();
                server.Dispose();
                await Task.WhenAll(sending, receiving);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
            {
                // Disconnects, including deliberate lost acknowledgements, are this fixture's transport fault.
            }
            finally { connections.TryRemove(id, out _); }
        }
    }

    private async Task ServerMessagesAsync(NetworkStream server, NetworkStream client, CancellationToken cancellationToken)
    {
        var header = new byte[5];
        while (true)
        {
            await server.ReadExactlyAsync(header, cancellationToken);
            var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1)) - 4;
            if (length is < 0 or > 16 * 1024 * 1024) throw new InvalidDataException("Invalid PostgreSQL message length.");
            var body = new byte[length];
            await server.ReadExactlyAsync(body, cancellationToken);
            if (header[0] == (byte)'C' && Encoding.UTF8.GetString(body) == "COMMIT\0" &&
                Interlocked.CompareExchange(ref dropCommit, 0, 1) == 1)
            {
                Interlocked.Increment(ref dropped);
                return;
            }
            await client.WriteAsync(header, cancellationToken);
            await client.WriteAsync(body, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        listener.Stop();
        SetAvailable(false);
        await accepting;
        await Task.WhenAll(workers);
        stopping.Dispose();
    }
}
