using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Xunit;

namespace InkMotion.Tests;

public sealed class LanCollaborationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"inkmotion-lan-test-{Guid.NewGuid():N}");

    public LanCollaborationTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("10.2.3.4", true)]
    [InlineData("172.31.5.2", true)]
    [InlineData("192.168.3.7", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    public void OnlyPrivateIpv4AddressesAreEligible(string address, bool expected) =>
        Assert.Equal(expected, LanNetworks.IsPrivate(IPAddress.Parse(address)));

    [Fact]
    public void InvitationAndSubnetValidationRejectInvalidOrPublicEndpoints()
    {
        var subnet = new LanAddress(IPAddress.Parse("192.168.1.2"),
            IPAddress.Parse("255.255.255.0"), "Test");
        Assert.True(subnet.Contains(IPAddress.Parse("192.168.1.100")));
        Assert.False(subnet.Contains(IPAddress.Parse("192.168.2.100")));
        var invitation = new LanInvitation("127.0.0.1", 1234,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        Assert.Equal(invitation, LanInvitation.Decode(invitation.Encode()));
        Assert.Throws<InvalidDataException>(() => LanInvitation.Decode("inkmotion-lan:not-an-invitation"));
        Assert.Throws<InvalidDataException>(() => LanInvitation.Decode((invitation with { Address = "8.8.8.8" }).Encode()));
    }

    [Fact]
    public async Task ProjectsUseContentIdentifiersAndRejectUnsafeMediaOrPlugins()
    {
        string media = Path.Combine(_root, "private-name.wav");
        await File.WriteAllTextAsync(media, "local media");
        var child = new EditorProject { AudioTracks = [new() { AudioPath = media }] };
        var project = new EditorProject
        {
            VideoTracks = [new() { Kind = TimelineTrackKind.Compound, Clips = [new()
                { NestedProject = child, NestedName = "Child", SourceDurationSeconds = 1, DurationSeconds = 1 }] }],
        };
        var store = new LanMediaStore(Path.Combine(_root, "cache"));
        LanProjectSnapshot snapshot = await store.PrepareAsync(project, _root, CancellationToken.None);
        Assert.DoesNotContain(_root, snapshot.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-name", snapshot.Json, StringComparison.OrdinalIgnoreCase);
        Assert.Single(snapshot.Assets);
        EditorProject resolved = await store.ResolveAsync(snapshot.Json,
            (_, _) => throw new InvalidOperationException("The file is already cached."), CancellationToken.None);
        string restored = resolved.VideoTracks[0].Clips[0].NestedProject!.AudioTracks[0].AudioPath!;
        Assert.Equal("local media", await File.ReadAllTextAsync(restored));
        Assert.Equal(media, child.AudioTracks[0].AudioPath);

        JsonNode node = JsonNode.Parse(snapshot.Json)!;
        var reference = ProjectMediaPaths.References(node).First();
        reference.Owner[reference.Key] = "..\\outside.wav";
        Assert.Throws<InvalidDataException>(() => LanMediaStore.ValidateWireProject(node.ToJsonString()));
        child.AudioTracks[0].Vst3Plugins.Add(new AudioVst3PluginProject());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PrepareAsync(project, _root, CancellationToken.None));
        child.AudioTracks[0].Vst3Plugins.Clear();
        child.AudioBuses.Add(new AudioBusProject { Vst3Plugins = [new AudioVst3PluginProject()] });
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PrepareAsync(project, _root, CancellationToken.None));
    }

    [Fact]
    public async Task ChangedLinkedMediaIsReimportedAndMissingMediaIsRefused()
    {
        string media = Path.Combine(_root, "source.wav");
        await File.WriteAllTextAsync(media, "first");
        DateTime firstWrite = File.GetLastWriteTimeUtc(media);
        var project = new EditorProject { AudioTracks = [new() { AudioPath = media }] };
        var store = new LanMediaStore(Path.Combine(_root, "cache"));

        LanProjectSnapshot first = await store.PrepareAsync(project, _root, CancellationToken.None);
        await File.WriteAllTextAsync(media, "other");
        File.SetLastWriteTimeUtc(media, firstWrite.AddSeconds(5));
        LanProjectSnapshot second = await store.PrepareAsync(project, _root, CancellationToken.None);

        Assert.NotEqual(Assert.Single(first.Assets), Assert.Single(second.Assets));
        Assert.Equal("other", await File.ReadAllTextAsync(store.PathFor(Assert.Single(second.Assets))));
        File.Delete(media);
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            store.PrepareAsync(project, _root, CancellationToken.None));
    }

    [Fact]
    public async Task CorruptTruncatedAndCancelledTransfersNeverPublishPartialFiles()
    {
        var store = new LanMediaStore(Path.Combine(_root, "cache"));
        string id = Convert.ToHexString(SHA256.HashData("expected"u8)) + ".wav";
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.ReceiveAsync(id, 3, new MemoryStream("bad"u8.ToArray()), CancellationToken.None));
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            store.ReceiveAsync(id, 10, new MemoryStream("short"u8.ToArray()), CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        using var blocked = new BlockingStream();
        Task receive = store.ReceiveAsync(id, 10, blocked, cancellation.Token);
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receive);
        Assert.False(store.Contains(id));
        Assert.Empty(Directory.GetFiles(store.DirectoryPath));
    }

    [Fact]
    public async Task StalledTransfersTimeOutWithoutPublishingPartialMedia()
    {
        var store = new LanMediaStore(Path.Combine(_root, "cache"));
        string id = Convert.ToHexString(SHA256.HashData("expected"u8)) + ".wav";
        using var blocked = new BlockingStream();
        await Assert.ThrowsAsync<TimeoutException>(() => LanProtocol.WithIdleTimeoutAsync(
            (token, progress) => store.ReceiveAsync(id, 10, blocked, token, progress),
            TimeSpan.FromMilliseconds(150), CancellationToken.None));
        Assert.False(store.Contains(id));
        Assert.Empty(Directory.GetFiles(store.DirectoryPath));

        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        using var cancelledStream = new BlockingStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LanProtocol.WithIdleTimeoutAsync(
            (token, progress) => store.ReceiveAsync(id, 10, cancelledStream, token, progress),
            TimeSpan.FromSeconds(5), cancelled.Token));
        Assert.Empty(Directory.GetFiles(store.DirectoryPath));
    }

    [Fact]
    public async Task ActiveTransfersExtendTheirIdleDeadline()
    {
        await LanProtocol.WithIdleTimeoutAsync(async (token, progress) =>
        {
            for (int step = 0; step < 5; step++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), token);
                progress();
            }
        }, TimeSpan.FromSeconds(1), CancellationToken.None);
    }

    [Fact]
    public async Task InvalidFrameLengthsAreRejectedBeforeAllocating()
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, LanProtocol.MaximumGreetingBytes + 1);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LanProtocol.ReadAsync(new MemoryStream(header), LanProtocol.MaximumGreetingBytes, CancellationToken.None));
    }

    [Fact]
    public async Task PeersShareMediaAndTakeTurnsWithoutOverwritingConcurrentEdits()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        string first = Path.Combine(_root, "first.wav");
        string second = Path.Combine(_root, "second.wav");
        await File.WriteAllTextAsync(first, "first media", timeout.Token);
        await File.WriteAllTextAsync(second, "second media", timeout.Token);
        var project = new EditorProject { AudioTracks = [new() { AudioPath = first }] };
        var hostMedia = new LanMediaStore(Path.Combine(_root, "host"));
        LanProjectSnapshot initial = await hostMedia.PrepareAsync(project, _root, timeout.Token);
        LanAddress loopback = LanNetworks.Available().First(address => IPAddress.IsLoopback(address.Address));
        await using var host = new LanCollaborationHost(loopback, hostMedia, initial.Json);
        await using var owner = await LanCollaborationClient.ConnectAsync(host.Invitation, hostMedia, timeout.Token);
        var ownerStates = Subscribe(owner);
        owner.Start();
        var firstMedia = new LanMediaStore(Path.Combine(_root, "guest-one"));
        await using var guestOne = await LanCollaborationClient.ConnectAsync(host.Invitation, firstMedia, timeout.Token);
        var firstStates = Subscribe(guestOne);
        guestOne.Start();
        var secondMedia = new LanMediaStore(Path.Combine(_root, "guest-two"));
        await using var guestTwo = await LanCollaborationClient.ConnectAsync(host.Invitation, secondMedia, timeout.Token);
        var secondStates = Subscribe(guestTwo);
        guestTwo.Start();

        Assert.Equal(owner.PeerId, guestOne.InitialState.EditorId);
        EditorProject opened = await guestOne.ResolveAsync(guestOne.InitialState.Project, timeout.Token);
        Assert.Equal("first media", await File.ReadAllTextAsync(opened.AudioTracks[0].AudioPath!, timeout.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => guestOne.ClaimAsync(0, timeout.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => guestOne.PublishAsync(opened, _root, 0, timeout.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.PublishAsync(project, _root, 99, timeout.Token));

        project.AudioTracks[0].AudioPath = second;
        project.Frames[0].ExposureFrames = 2;
        Assert.Equal(1, (await owner.PublishAsync(project, _root, 0, timeout.Token)).Revision);
        LanState updateOne = await NextAsync(firstStates, state => state.Revision == 1, timeout.Token);
        LanState updateTwo = await NextAsync(secondStates, state => state.Revision == 1, timeout.Token);
        EditorProject guestProject = await guestOne.ResolveAsync(updateOne.Project, timeout.Token);
        EditorProject secondProject = await guestTwo.ResolveAsync(updateTwo.Project, timeout.Token);
        Assert.Equal("second media", await File.ReadAllTextAsync(guestProject.AudioTracks[0].AudioPath!, timeout.Token));
        Assert.Equal("second media", await File.ReadAllTextAsync(secondProject.AudioTracks[0].AudioPath!, timeout.Token));
        Assert.NotEqual(guestProject.AudioTracks[0].AudioPath, secondProject.AudioTracks[0].AudioPath);

        await owner.ReleaseAsync(1, timeout.Token);
        await NextAsync(firstStates, state => state.Revision == 1 && state.EditorId is null, timeout.Token);
        await NextAsync(secondStates, state => state.Revision == 1 && state.EditorId is null, timeout.Token);
        bool[] claims = await Task.WhenAll(TryClaimAsync(guestOne, 1, timeout.Token), TryClaimAsync(guestTwo, 1, timeout.Token));
        Assert.Single(claims.Where(claimed => claimed));
        LanCollaborationClient winner = claims[0] ? guestOne : guestTwo;
        EditorProject winningProject = claims[0] ? guestProject : secondProject;
        winningProject.Frames[0].ExposureFrames = 3;
        Assert.Equal(2, (await winner.PublishAsync(winningProject, _root, 1, timeout.Token)).Revision);
        LanState ownerUpdate = await NextAsync(ownerStates, state => state.Revision == 2, timeout.Token);
        EditorProject latest = await owner.ResolveAsync(ownerUpdate.Project, timeout.Token);
        Assert.Equal(3, latest.Frames[0].ExposureFrames);
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.PublishAsync(project, _root, 1, timeout.Token));

        await winner.DisposeAsync();
        await NextAsync(ownerStates, state => state.Revision == 2 && state.EditorId is null, timeout.Token);
        var reconnectMedia = new LanMediaStore(Path.Combine(_root, "reconnected"));
        await using var reconnected = await LanCollaborationClient.ConnectAsync(host.Invitation, reconnectMedia, timeout.Token);
        reconnected.Start();
        Assert.Equal(2, reconnected.InitialState.Revision);
        Assert.Equal(2, (await reconnected.ClaimAsync(2, timeout.Token)).Revision);
        EditorProject recovered = await reconnected.ResolveAsync(reconnected.InitialState.Project, timeout.Token);
        Assert.Equal(3, recovered.Frames[0].ExposureFrames);
        Assert.Equal("second media", await File.ReadAllTextAsync(recovered.AudioTracks[0].AudioPath!, timeout.Token));
    }

    [Fact]
    public async Task InvitationSecretAndPinnedCertificateAreRequired()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var media = new LanMediaStore(Path.Combine(_root, "host"));
        LanProjectSnapshot snapshot = await media.PrepareAsync(new EditorProject(), _root, timeout.Token);
        await using var host = new LanCollaborationHost(LanNetworks.Available().First(
            address => IPAddress.IsLoopback(address.Address)), media, snapshot.Json);
        var guestMedia = new LanMediaStore(Path.Combine(_root, "guest"));
        LanInvitation wrongSecret = host.Invitation with { Secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LanCollaborationClient.ConnectAsync(wrongSecret, guestMedia, timeout.Token));
        LanInvitation wrongPin = host.Invitation with { Fingerprint = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) };
        Exception? error = await Record.ExceptionAsync(() =>
            LanCollaborationClient.ConnectAsync(wrongPin, guestMedia, timeout.Token));
        Assert.True(error is AuthenticationException or IOException, $"Unexpected error: {error}");
    }

    [Fact]
    public async Task UnresponsiveHandshakeTimesOutAndUserCancellationRemainsDistinct()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var invitation = new LanInvitation("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                LanProtocol.ConnectAsync(invitation, cancellation.Token));
            await Assert.ThrowsAsync<TimeoutException>(() =>
                LanProtocol.ConnectAsync(invitation, CancellationToken.None));
        }
        finally { listener.Stop(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnansweredRequestClosesTheControlConnection(bool cancel)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(85));
        using var serverStop = new CancellationTokenSource();
        using X509Certificate2 certificate = LanProtocol.CreateCertificate();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var invitation = new LanInvitation("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
            Convert.ToHexString(SHA256.HashData(certificate.RawData)),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var media = new LanMediaStore(Path.Combine(_root, "client"));
        LanProjectSnapshot project = await media.PrepareAsync(new EditorProject(), _root, deadline.Token);
        string peerId = Guid.NewGuid().ToString("N");
        Task server = Task.Run(async () =>
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync(serverStop.Token);
            using var stream = new SslStream(peer.GetStream());
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, serverStop.Token);
            LanMessage join = await LanProtocol.ReadAsync(stream, LanProtocol.MaximumGreetingBytes, serverStop.Token);
            Assert.Equal("join", join.Type);
            await LanProtocol.WriteAsync(stream, new LanMessage
            {
                Type = "welcome",
                PeerId = peerId,
                PeerKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                EditorId = peerId,
                Project = project.Json,
                PeerCount = 1,
            }, serverStop.Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, serverStop.Token);
        }, serverStop.Token);
        try
        {
            await using var client = await LanCollaborationClient.ConnectAsync(invitation, media, deadline.Token);
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Disconnected += _ => disconnected.TrySetResult();
            client.Start();
            if (cancel)
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ClaimAsync(0, cancellation.Token));
            }
            else
            {
                await Assert.ThrowsAsync<TimeoutException>(() => client.ClaimAsync(0, deadline.Token));
            }
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            serverStop.Cancel();
            listener.Stop();
            try { await server; }
            catch (OperationCanceledException) when (serverStop.IsCancellationRequested) { }
        }
    }

    private static ChannelReader<LanState> Subscribe(LanCollaborationClient client)
    {
        Channel<LanState> channel = Channel.CreateUnbounded<LanState>();
        client.StateChanged += state => channel.Writer.TryWrite(state);
        return channel.Reader;
    }

    private static async Task<LanState> NextAsync(
        ChannelReader<LanState> states, Func<LanState, bool> predicate, CancellationToken cancellationToken)
    {
        while (true)
        {
            LanState state = await states.ReadAsync(cancellationToken);
            if (predicate(state)) return state;
        }
    }

    private static async Task<bool> TryClaimAsync(LanCollaborationClient client, long revision, CancellationToken token)
    {
        try { await client.ClaimAsync(revision, token); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private sealed class BlockingStream : Stream
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
