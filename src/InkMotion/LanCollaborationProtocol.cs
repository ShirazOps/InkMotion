using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace InkMotion;

internal sealed record LanAddress(IPAddress Address, IPAddress Mask, string Description)
{
    internal bool Contains(IPAddress remote)
    {
        if (remote.AddressFamily != AddressFamily.InterNetwork || !LanNetworks.IsPrivate(remote))
            return false;
        byte[] address = Address.GetAddressBytes();
        byte[] mask = Mask.GetAddressBytes();
        byte[] other = remote.GetAddressBytes();
        return Enumerable.Range(0, 4).All(index => (address[index] & mask[index]) == (other[index] & mask[index]));
    }

    public override string ToString() => $"{Description} ({Address}, subnet mask {Mask})";
}

internal static class LanNetworks
{
    internal static IReadOnlyList<LanAddress> Available()
    {
        var addresses = new List<LanAddress>
        {
            new(IPAddress.Loopback, IPAddress.Parse("255.0.0.0"), "This computer only"),
        };
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up))
        {
            foreach (UnicastIPAddressInformation item in adapter.GetIPProperties().UnicastAddresses)
            {
                if (item.Address.AddressFamily != AddressFamily.InterNetwork ||
                    IPAddress.IsLoopback(item.Address) || !IsPrivate(item.Address))
                    continue;
                IPAddress? mask = item.IPv4Mask;
                if (mask is null || mask.Equals(IPAddress.Any)) continue;
                addresses.Add(new(item.Address, mask, adapter.Name));
            }
        }
        return addresses.DistinctBy(item => item.Address).ToArray();
    }

    internal static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
            (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168) ||
            (bytes[0] == 169 && bytes[1] == 254) ||
            bytes[0] == 127;
    }

    internal static bool IsReachableLocally(IPAddress address) =>
        Available().Any(local => local.Contains(address));
}

internal sealed record LanInvitation(string Address, int Port, string Fingerprint, string Secret)
{
    private const string Prefix = "inkmotion-lan:";

    internal string Encode()
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(this);
        return Prefix + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static LanInvitation Decode(string value)
    {
        if (value is null || value.Length > 2048 || !value.StartsWith(Prefix, StringComparison.Ordinal))
            throw new InvalidDataException("The collaboration invitation is invalid.");
        string encoded = value[Prefix.Length..].Replace('-', '+').Replace('_', '/');
        encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
        try
        {
            LanInvitation invitation = JsonSerializer.Deserialize<LanInvitation>(Convert.FromBase64String(encoded))
                ?? throw new InvalidDataException("The collaboration invitation is empty.");
            if (!IPAddress.TryParse(invitation.Address, out IPAddress? address) ||
                !LanNetworks.IsPrivate(address) || invitation.Port is < 1 or > 65535 ||
                invitation.Fingerprint is null || invitation.Fingerprint.Length != 64 ||
                Convert.FromHexString(invitation.Fingerprint).Length != 32 ||
                invitation.Secret is null || Convert.FromBase64String(invitation.Secret).Length != 32)
                throw new InvalidDataException("The collaboration invitation is invalid.");
            return invitation;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            throw new InvalidDataException("The collaboration invitation is invalid.", ex);
        }
    }
}

internal sealed class LanMessage
{
    public string Type { get; set; } = string.Empty;
    public string? Secret { get; set; }
    public string? PeerId { get; set; }
    public string? PeerKey { get; set; }
    public string? EditorId { get; set; }
    public string? Project { get; set; }
    public string? AssetId { get; set; }
    public string? Error { get; set; }
    public long Revision { get; set; }
    public long Generation { get; set; }
    public long Request { get; set; }
    public long Length { get; set; }
    public int PeerCount { get; set; }
}

internal sealed record LanState(long Revision, long Generation, string Project, string? EditorId, int PeerCount);
internal sealed record LanResult(long Revision, long Generation, string? EditorId);

internal static class LanProtocol
{
    internal const int MaximumMessageBytes = 128 * 1024 * 1024;
    internal const int MaximumGreetingBytes = 4096;
    internal static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(60);

    internal static async Task WriteAsync(Stream stream, LanMessage message, CancellationToken cancellationToken)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(message);
        if (json.Length > MaximumMessageBytes)
            throw new InvalidDataException("A collaboration message is too large.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, json.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(json, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    internal static async Task<LanMessage> ReadAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > maximumBytes)
            throw new InvalidDataException("A collaboration message has an invalid length.");
        byte[] json = new byte[length];
        await stream.ReadExactlyAsync(json, cancellationToken);
        return JsonSerializer.Deserialize<LanMessage>(json)
            ?? throw new InvalidDataException("A collaboration message is empty.");
    }

    internal static bool SecretMatches(string? actual, string expected)
    {
        if (actual is null) return false;
        byte[] a = Encoding.UTF8.GetBytes(actual);
        byte[] b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    internal static X509Certificate2 CreateCertificate()
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=InkMotion local collaboration", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        byte[] certificate = generated.Export(X509ContentType.Pfx);
        try { return X509CertificateLoader.LoadPkcs12(certificate, null, X509KeyStorageFlags.UserKeySet); }
        finally { CryptographicOperations.ZeroMemory(certificate); }
    }

    internal static async Task<(TcpClient Client, SslStream Stream)> ConnectAsync(
        LanInvitation invitation, CancellationToken cancellationToken)
    {
        IPAddress address = IPAddress.Parse(invitation.Address);
        if (!LanNetworks.IsReachableLocally(address))
            throw new InvalidOperationException("The host is not on a directly connected private IPv4 subnet.");
        var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(HandshakeTimeout);
            await client.ConnectAsync(address, invitation.Port, timeout.Token).ConfigureAwait(false);
            byte[] fingerprint = Convert.FromHexString(invitation.Fingerprint);
            var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, certificate, _, _) =>
            {
                if (certificate is null) return false;
                byte[] actual = SHA256.HashData(certificate.GetRawCertData());
                return CryptographicOperations.FixedTimeEquals(actual, fingerprint);
            });
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "InkMotion local collaboration",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, timeout.Token).ConfigureAwait(false);
            return (client, stream);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException("Connecting to the collaboration host timed out.", ex);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    internal static async Task WithIdleTimeoutAsync(
        Func<CancellationToken, Action, Task> operation, TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(interval);
        try
        {
            await operation(timeout.Token, () => timeout.CancelAfter(interval)).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The collaboration media transfer stopped responding.", ex);
        }
    }

    internal static async Task CopyWithProgressAsync(
        Stream source, Stream destination, Action progress, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            progress();
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            progress();
        }
    }
}
