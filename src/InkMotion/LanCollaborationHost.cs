using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Channels;

namespace InkMotion;

internal sealed class LanCollaborationHost : IAsyncDisposable
{
    private const int MaximumPeers = 8;
    private readonly LanAddress _address;
    private readonly LanMediaStore _media;
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _certificate;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _connectionsLimit = new(24, 24);
    private readonly ConcurrentDictionary<int, TcpClient> _connections = new();
    private readonly ConcurrentDictionary<int, Task> _connectionTasks = new();
    private readonly Dictionary<string, Peer> _peers = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly Task _acceptTask;
    private string _project;
    private string? _editorId;
    private long _revision;
    private long _generation;
    private int _nextConnection;
    private bool _firstPeerJoined;
    private bool _disposed;

    private sealed class Peer(string id, string key, TcpClient connection)
    {
        internal string Id { get; } = id;
        internal string Key { get; } = key;
        internal TcpClient Connection { get; } = connection;
        internal Channel<LanMessage> Outgoing { get; } = Channel.CreateBounded<LanMessage>(new BoundedChannelOptions(8)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        internal void Send(LanMessage message)
        {
            if (!Outgoing.Writer.TryWrite(message))
                Connection.Close();
        }
    }

    internal LanCollaborationHost(LanAddress address, LanMediaStore media, string project)
    {
        if (!LanNetworks.Available().Any(item => item.Address.Equals(address.Address) && item.Mask.Equals(address.Mask)))
            throw new InvalidOperationException("The selected private network address is no longer available.");
        LanMediaStore.ValidateWireProject(project, media.Contains);
        _address = address;
        _media = media;
        _project = project;
        _certificate = LanProtocol.CreateCertificate();
        _listener = new TcpListener(address.Address, 0);
        try
        {
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Invitation = new(address.Address.ToString(), port,
                Convert.ToHexString(SHA256.HashData(_certificate.RawData)),
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            _acceptTask = Task.Run(AcceptAsync);
        }
        catch
        {
            _certificate.Dispose();
            _listener.Stop();
            throw;
        }
    }

    internal LanInvitation Invitation { get; }
    internal event Action<string>? Faulted;

    internal LanState CurrentState
    {
        get { lock (_gate) return State(); }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                if (!_connectionsLimit.Wait(0))
                {
                    client.Dispose();
                    continue;
                }
                int id = Interlocked.Increment(ref _nextConnection);
                _connections[id] = client;
                Task task = HandleConnectionAsync(client);
                _connectionTasks[id] = task;
                _ = ObserveConnectionAsync(id, task);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            Faulted?.Invoke($"The local collaboration listener stopped: {ex.Message}");
            _stop.Cancel();
        }
    }

    private async Task ObserveConnectionAsync(int id, Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            if (!_stop.IsCancellationRequested)
                Faulted?.Invoke($"A collaboration connection failed: {ex.Message}");
        }
        finally
        {
            _connections.TryRemove(id, out _);
            _connectionTasks.TryRemove(id, out _);
            _connectionsLimit.Release();
        }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        using (client)
        {
            if (client.Client.RemoteEndPoint is not IPEndPoint remote || !_address.Contains(remote.Address))
                return;
            using var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(LanProtocol.HandshakeTimeout);
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                }, timeout.Token);
                LanMessage greeting = await LanProtocol.ReadAsync(stream, LanProtocol.MaximumGreetingBytes, timeout.Token);
                if (!LanProtocol.SecretMatches(greeting.Secret, Invitation.Secret))
                {
                    await LanProtocol.WriteAsync(stream, new() { Type = "error", Error = "The invitation is not valid." }, timeout.Token);
                    return;
                }
                switch (greeting.Type)
                {
                    case "join":
                        await RunPeerAsync(client, stream);
                        break;
                    case "upload":
                        await ReceiveAssetAsync(greeting, stream);
                        break;
                    case "download":
                        await SendAssetAsync(greeting, stream);
                        break;
                    default:
                        await LanProtocol.WriteAsync(stream, new() { Type = "error", Error = "Unknown collaboration request." }, timeout.Token);
                        break;
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (EndOfStreamException) { }
            catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or
                InvalidDataException or JsonException or OperationCanceledException or ObjectDisposedException)
            {
                if (!_stop.IsCancellationRequested)
                    Faulted?.Invoke($"A collaboration connection ended: {ex.Message}");
            }
        }
    }

    private async Task RunPeerAsync(TcpClient client, SslStream stream)
    {
        Peer? peer;
        lock (_gate)
        {
            if (_peers.Count >= MaximumPeers)
            {
                peer = null;
            }
            else
            {
                peer = new Peer(Guid.NewGuid().ToString("N"),
                    Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), client);
                _peers.Add(peer.Id, peer);
                if (!_firstPeerJoined)
                {
                    _firstPeerJoined = true;
                    _editorId = peer.Id;
                }
                _generation = checked(_generation + 1);
                peer.Send(new LanMessage
                {
                    Type = "welcome", PeerId = peer.Id, PeerKey = peer.Key,
                    EditorId = _editorId, Project = _project, Revision = _revision,
                    Generation = _generation, PeerCount = _peers.Count,
                });
                BroadcastState(peer.Id);
            }
        }
        if (peer is null)
        {
            await LanProtocol.WriteAsync(stream, new() { Type = "error", Error = "The session already has eight collaborators." }, _stop.Token);
            return;
        }

        using var peerStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        Task writer = WritePeerAsync(peer, stream, peerStop.Token);
        try
        {
            while (!peerStop.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(peerStop.Token);
                timeout.CancelAfter(LanProtocol.PeerTimeout);
                LanMessage message = await LanProtocol.ReadAsync(stream, LanProtocol.MaximumMessageBytes, timeout.Token);
                HandlePeerMessage(peer, message);
            }
        }
        finally
        {
            peerStop.Cancel();
            peer.Outgoing.Writer.TryComplete();
            client.Close();
            try { await writer; }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or SocketException) { }
            lock (_gate)
            {
                _peers.Remove(peer.Id);
                if (_editorId == peer.Id) _editorId = null;
                _generation = checked(_generation + 1);
                BroadcastState();
            }
        }
    }

    private static async Task WritePeerAsync(Peer peer, Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (LanMessage message in peer.Outgoing.Reader.ReadAllAsync(cancellationToken))
                await LanProtocol.WriteAsync(stream, message, cancellationToken);
        }
        catch
        {
            peer.Connection.Close();
            throw;
        }
    }

    private void HandlePeerMessage(Peer peer, LanMessage message)
    {
        lock (_gate)
        {
            string? error = null;
            switch (message.Type)
            {
                case "ping":
                    break;
                case "publish":
                    if (_editorId != peer.Id)
                        error = "Only the collaborator holding the editing turn can publish changes.";
                    else if (message.Revision != _revision)
                        error = "The project revision changed. Local edits have not been overwritten.";
                    else if (message.Project is null)
                        error = "The proposed project is empty.";
                    else
                    {
                        try
                        {
                            LanMediaStore.ValidateWireProject(message.Project, _media.Contains);
                            _project = message.Project;
                            _revision = checked(_revision + 1);
                            _generation = checked(_generation + 1);
                        }
                        catch (Exception ex) when (ex is InvalidDataException or JsonException or
                            InvalidOperationException or ArgumentException or FormatException or OverflowException)
                        {
                            error = $"The proposed project was refused: {ex.Message}";
                        }
                        if (error is null) BroadcastState(peer.Id);
                    }
                    break;
                case "claim":
                    if (message.Revision != _revision)
                        error = "Wait for the latest project before taking the editing turn.";
                    else if (_editorId is not null)
                        error = "Another collaborator holds the editing turn.";
                    else
                    {
                        _editorId = peer.Id;
                        _generation = checked(_generation + 1);
                        BroadcastState();
                    }
                    break;
                case "release":
                    if (_editorId != peer.Id || message.Revision != _revision)
                        error = "The editing turn could not be released; local edits have not been overwritten.";
                    else
                    {
                        _editorId = null;
                        _generation = checked(_generation + 1);
                        BroadcastState();
                    }
                    break;
                default:
                    error = "Unknown collaboration command.";
                    break;
            }
            peer.Send(new LanMessage
            {
                Type = "result", Request = message.Request, Revision = _revision,
                Generation = _generation, EditorId = _editorId, Error = error,
            });
        }
    }

    private LanState State() => new(_revision, _generation, _project, _editorId, _peers.Count);

    private void BroadcastState(string? except = null)
    {
        var message = new LanMessage
        {
            Type = "state", Project = _project, Revision = _revision,
            Generation = _generation, EditorId = _editorId, PeerCount = _peers.Count,
        };
        foreach (Peer peer in _peers.Values)
            if (peer.Id != except) peer.Send(message);
    }

    private bool IsAuthorisedPeer(LanMessage message, bool requireEditor)
    {
        lock (_gate)
        {
            return message.PeerId is not null && _peers.TryGetValue(message.PeerId, out Peer? peer) &&
                LanProtocol.SecretMatches(message.PeerKey, peer.Key) &&
                (!requireEditor || _editorId == peer.Id);
        }
    }

    private async Task ReceiveAssetAsync(LanMessage message, Stream stream)
    {
        if (!IsAuthorisedPeer(message, requireEditor: true) || !LanMediaStore.IsAssetId(message.AssetId) ||
            message.Length < 0 || message.Length > LanMediaStore.MaximumMediaBytes)
        {
            await LanProtocol.WriteAsync(stream, new() { Type = "error", Error = "The media upload was not authorised." }, _stop.Token);
            return;
        }
        await LanProtocol.WithIdleTimeoutAsync(async (token, progress) =>
        {
            string id = message.AssetId!;
            if (_media.Contains(id))
            {
                await LanProtocol.WriteAsync(stream, new() { Type = "exists" }, token);
                return;
            }
            await LanProtocol.WriteAsync(stream, new() { Type = "ready" }, token);
            progress();
            try
            {
                await _media.ReceiveAsync(id, message.Length, stream, token, progress);
                await LanProtocol.WriteAsync(stream, new() { Type = "stored" }, token);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException)
            {
                Faulted?.Invoke($"A collaboration upload failed: {ex.Message}");
                await LanProtocol.WriteAsync(stream, new() { Type = "error", Error = "The host could not store or verify the media." }, token);
            }
        }, LanProtocol.PeerTimeout, _stop.Token);
    }

    private async Task SendAssetAsync(LanMessage message, Stream stream)
    {
        if (!IsAuthorisedPeer(message, requireEditor: false) || !LanMediaStore.IsAssetId(message.AssetId) ||
            !_media.Contains(message.AssetId!))
        {
            await LanProtocol.WriteAsync(stream, new() { Type = "error", Error = "The requested media is unavailable." }, _stop.Token);
            return;
        }
        await LanProtocol.WithIdleTimeoutAsync(async (token, progress) =>
        {
            using var file = new FileStream(_media.PathFor(message.AssetId!), FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await LanProtocol.WriteAsync(stream, new() { Type = "ready", Length = file.Length }, token);
            progress();
            await LanProtocol.CopyWithProgressAsync(file, stream, progress, token);
            await stream.FlushAsync(token);
        }, LanProtocol.PeerTimeout, _stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        _listener.Stop();
        foreach (TcpClient client in _connections.Values) client.Close();
        await _acceptTask;
        await Task.WhenAll(_connectionTasks.Values.ToArray());
        _certificate.Dispose();
        _stop.Dispose();
    }
}
