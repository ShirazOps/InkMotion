using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;

namespace InkMotion;

internal sealed class LanCollaborationClient : IAsyncDisposable
{
    private readonly LanInvitation _invitation;
    private readonly LanMediaStore _media;
    private readonly TcpClient _connection;
    private readonly SslStream _stream;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<LanMessage>> _requests = new();
    private readonly HashSet<string> _hostAssets = new(StringComparer.Ordinal);
    private readonly object _assetsGate = new();
    private Task? _reader;
    private Task? _heartbeat;
    private long _nextRequest;
    private volatile bool _closing;

    private LanCollaborationClient(LanInvitation invitation, LanMediaStore media,
        TcpClient connection, SslStream stream, LanMessage welcome)
    {
        _invitation = invitation;
        _media = media;
        _connection = connection;
        _stream = stream;
        PeerId = welcome.PeerId!;
        PeerKey = welcome.PeerKey!;
        InitialState = ReadState(welcome);
    }

    internal string PeerId { get; }
    private string PeerKey { get; }
    internal LanState InitialState { get; }
    internal event Action<LanState>? StateChanged;
    internal event Action<string>? Disconnected;

    internal static async Task<LanCollaborationClient> ConnectAsync(
        LanInvitation invitation, LanMediaStore media, CancellationToken cancellationToken)
    {
        (TcpClient client, SslStream stream) = await LanProtocol.ConnectAsync(invitation, cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(LanProtocol.HandshakeTimeout);
            await LanProtocol.WriteAsync(stream, new() { Type = "join", Secret = invitation.Secret }, timeout.Token);
            LanMessage welcome = await LanProtocol.ReadAsync(stream, LanProtocol.MaximumMessageBytes, timeout.Token);
            if (welcome.Type == "error")
                throw new InvalidOperationException(welcome.Error ?? "The host refused the connection.");
            if (welcome.Type != "welcome" || !Guid.TryParseExact(welcome.PeerId, "N", out _) ||
                welcome.PeerKey is null || Convert.FromBase64String(welcome.PeerKey).Length != 32)
                throw new InvalidDataException("The host sent an invalid collaboration greeting.");
            return new(invitation, media, client, stream, welcome);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            stream.Dispose();
            client.Dispose();
            throw new TimeoutException("The collaboration host did not complete the greeting in time.", ex);
        }
        catch
        {
            stream.Dispose();
            client.Dispose();
            throw;
        }
    }

    internal void Start()
    {
        if (_reader is not null) throw new InvalidOperationException("The collaboration connection has already started.");
        _reader = Task.Run(ReadAsync);
        _heartbeat = Task.Run(HeartbeatAsync);
    }

    internal Task<LanResult> PublishAsync(
        EditorProject project, string baseDirectory, long revision, CancellationToken cancellationToken) =>
        Task.Run(() => PublishCoreAsync(project, baseDirectory, revision, cancellationToken), cancellationToken);

    private async Task<LanResult> PublishCoreAsync(
        EditorProject project, string baseDirectory, long revision, CancellationToken cancellationToken)
    {
        LanProjectSnapshot snapshot = await _media.PrepareAsync(project, baseDirectory, cancellationToken);
        foreach (string id in snapshot.Assets)
        {
            bool known;
            lock (_assetsGate) known = _hostAssets.Contains(id);
            if (!known)
            {
                await UploadAsync(id, cancellationToken);
                lock (_assetsGate) _hostAssets.Add(id);
            }
        }
        LanMessage result = await RequestAsync(new()
        {
            Type = "publish", Project = snapshot.Json, Revision = revision,
        }, cancellationToken);
        return new(result.Revision, result.Generation, result.EditorId);
    }

    internal Task<EditorProject> ResolveAsync(string project, CancellationToken cancellationToken) =>
        Task.Run(() => _media.ResolveAsync(project, DownloadAsync, cancellationToken), cancellationToken);

    internal async Task<LanResult> ClaimAsync(long revision, CancellationToken cancellationToken)
    {
        LanMessage result = await RequestAsync(new() { Type = "claim", Revision = revision }, cancellationToken);
        return new(result.Revision, result.Generation, result.EditorId);
    }

    internal async Task<LanResult> ReleaseAsync(long revision, CancellationToken cancellationToken)
    {
        LanMessage result = await RequestAsync(new() { Type = "release", Revision = revision }, cancellationToken);
        return new(result.Revision, result.Generation, result.EditorId);
    }

    private async Task<LanMessage> RequestAsync(LanMessage message, CancellationToken cancellationToken)
    {
        if (_reader is null) throw new InvalidOperationException("The collaboration connection has not started.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        linked.CancelAfter(LanProtocol.PeerTimeout);
        long request = Interlocked.Increment(ref _nextRequest);
        message.Request = request;
        var completion = new TaskCompletionSource<LanMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_requests.TryAdd(request, completion))
            throw new InvalidOperationException("The collaboration request could not be queued.");
        try
        {
            await _sendGate.WaitAsync(linked.Token);
            try { await LanProtocol.WriteAsync(_stream, message, linked.Token); }
            finally { _sendGate.Release(); }
            LanMessage result = await completion.Task.WaitAsync(linked.Token);
            if (result.Error is not null) throw new InvalidOperationException(result.Error);
            if (result.Revision < 0 || result.Generation < 0 ||
                (result.EditorId is not null && !Guid.TryParseExact(result.EditorId, "N", out _)))
                throw new InvalidDataException("The host sent an invalid collaboration response.");
            return result;
        }
        catch (OperationCanceledException ex)
        {
            bool stopped = _stop.IsCancellationRequested;
            _connection.Close();
            if (cancellationToken.IsCancellationRequested) throw;
            if (stopped) throw new IOException("The collaboration connection closed before the request completed.", ex);
            throw new TimeoutException("The collaboration host did not respond in time; the connection was closed.", ex);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            _connection.Close();
            throw;
        }
        finally
        {
            _requests.TryRemove(request, out _);
        }
    }

    private async Task ReadAsync()
    {
        string reason = "The collaboration connection closed.";
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                LanMessage message = await LanProtocol.ReadAsync(_stream, LanProtocol.MaximumMessageBytes, _stop.Token);
                switch (message.Type)
                {
                    case "result":
                        if (_requests.TryRemove(message.Request, out TaskCompletionSource<LanMessage>? request))
                            request.TrySetResult(message);
                        break;
                    case "state":
                        StateChanged?.Invoke(ReadState(message));
                        break;
                    default:
                        throw new InvalidDataException("The host sent an unknown collaboration message.");
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            reason = $"The collaboration connection ended: {ex.Message}";
        }
        finally
        {
            _stop.Cancel();
            _connection.Close();
            foreach (TaskCompletionSource<LanMessage> request in _requests.Values)
                request.TrySetException(new IOException(reason));
            if (!_closing) Disconnected?.Invoke(reason);
        }
    }

    private LanState ReadState(LanMessage message)
    {
        if (message.Revision < 0 || message.Generation < 0 || message.Project is null || message.PeerCount is < 1 or > 8 ||
            (message.EditorId is not null && !Guid.TryParseExact(message.EditorId, "N", out _)))
            throw new InvalidDataException("The host sent an invalid collaboration state.");
        IReadOnlySet<string> assets = LanMediaStore.ValidateWireProject(message.Project);
        lock (_assetsGate) _hostAssets.UnionWith(assets);
        return new(message.Revision, message.Generation, message.Project, message.EditorId, message.PeerCount);
    }

    private async Task HeartbeatAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), _stop.Token);
                await RequestAsync(new() { Type = "ping" }, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_stop.IsCancellationRequested)
            {
                _connection.Close();
                Disconnected?.Invoke($"The collaboration heartbeat failed: {ex.Message}");
            }
        }
    }

    private LanMessage AssetRequest(string type, string id) => new()
    {
        Type = type, Secret = _invitation.Secret, PeerId = PeerId, PeerKey = PeerKey, AssetId = id,
    };

    private async Task UploadAsync(string id, CancellationToken cancellationToken)
    {
        string path = _media.PathFor(id);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        (TcpClient connection, SslStream stream) = await LanProtocol.ConnectAsync(_invitation, cancellationToken);
        using (connection)
        using (stream)
        {
            await LanProtocol.WithIdleTimeoutAsync(async (token, progress) =>
            {
                LanMessage request = AssetRequest("upload", id);
                request.Length = file.Length;
                await LanProtocol.WriteAsync(stream, request, token);
                progress();
                LanMessage response = await LanProtocol.ReadAsync(stream, LanProtocol.MaximumGreetingBytes, token);
                progress();
                if (response.Type == "exists") return;
                if (response.Type != "ready")
                    throw new InvalidDataException(response.Error ?? "The host refused the media upload.");
                await LanProtocol.CopyWithProgressAsync(file, stream, progress, token);
                await stream.FlushAsync(token);
                progress();
                response = await LanProtocol.ReadAsync(stream, LanProtocol.MaximumGreetingBytes, token);
                if (response.Type != "stored")
                    throw new InvalidDataException(response.Error ?? "The host could not verify the media upload.");
            }, LanProtocol.PeerTimeout, cancellationToken);
        }
    }

    private async Task DownloadAsync(string id, CancellationToken cancellationToken)
    {
        (TcpClient connection, SslStream stream) = await LanProtocol.ConnectAsync(_invitation, cancellationToken);
        using (connection)
        using (stream)
        {
            await LanProtocol.WithIdleTimeoutAsync(async (token, progress) =>
            {
                await LanProtocol.WriteAsync(stream, AssetRequest("download", id), token);
                progress();
                LanMessage response = await LanProtocol.ReadAsync(stream, LanProtocol.MaximumGreetingBytes, token);
                progress();
                if (response.Type != "ready" || response.Length < 0 || response.Length > LanMediaStore.MaximumMediaBytes)
                    throw new InvalidDataException(response.Error ?? "The host could not send the requested media.");
                await _media.ReceiveAsync(id, response.Length, stream, token, progress);
            }, LanProtocol.PeerTimeout, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_closing) return;
        _closing = true;
        _stop.Cancel();
        _connection.Close();
        if (_reader is not null) await _reader;
        if (_heartbeat is not null) await _heartbeat;
        _stream.Dispose();
        _connection.Dispose();
        _sendGate.Dispose();
        _stop.Dispose();
    }
}
