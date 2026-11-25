using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace InkMotion;

/// <summary>
/// An openly licensed ONNX model that InkMotion can run on this device through Windows ML. Models are never bundled
/// or fetched automatically: the user imports a local file or explicitly agrees to a download from a pinned URL.
/// </summary>
/// <param name="DownloadUrl">Pinned HTTPS location of the published export, or null until a maintainer publishes it.</param>
/// <param name="Sha256">Lower-case hexadecimal SHA-256 of the published export, or null until it is published.</param>
public sealed record OnDeviceModelDescriptor(
    string Id,
    string DisplayName,
    string FileName,
    string Licence,
    string SourceProject,
    string Purpose,
    string? DownloadUrl,
    string? Sha256,
    long MaximumBytes)
{
    public bool IsPublished => DownloadUrl is not null && Sha256 is not null;
}

/// <summary>Proof that the user agreed to download one specific model in a consent dialog.</summary>
public sealed class OnDeviceModelDownloadConsent
{
    private OnDeviceModelDownloadConsent(string modelId, DateTimeOffset grantedAt)
    {
        ModelId = modelId;
        GrantedAt = grantedAt;
    }

    public string ModelId { get; }
    public DateTimeOffset GrantedAt { get; }

    /// <summary>Call only after the user has explicitly accepted the download dialog for this model.</summary>
    public static OnDeviceModelDownloadConsent GrantedByUser(OnDeviceModelDescriptor model) =>
        new(model.Id, DateTimeOffset.UtcNow);
}

public static class OnDeviceModels
{
    public static readonly OnDeviceModelDescriptor PoseEstimator = new(
        "rtmpose-m-coco",
        "RTMPose-m body pose (COCO 17 keypoints, 256 × 192)",
        "rtmpose-m-coco-256x192.onnx",
        "Apache-2.0 (OpenMMLab MMPose code and weights). Use weights trained on COCO only; COCO annotations are CC BY 4.0.",
        "https://github.com/open-mmlab/mmpose/tree/main/projects/rtmpose",
        "Finds body joints for articulated limb tracking.",
        null, null, 200L * 1024 * 1024);

    public static readonly OnDeviceModelDescriptor SegmentationEncoder = new(
        "mobilesam-encoder",
        "MobileSAM image encoder (1024 × 1024)",
        "mobilesam-encoder.onnx",
        "Apache-2.0 (MobileSAM code and weights, ChaoningZhang/MobileSAM).",
        "https://github.com/ChaoningZhang/MobileSAM",
        "Summarises each video frame for automatic occlusion mattes.",
        null, null, 300L * 1024 * 1024);

    public static readonly OnDeviceModelDescriptor SegmentationDecoder = new(
        "mobilesam-decoder",
        "MobileSAM mask decoder",
        "mobilesam-decoder.onnx",
        "Apache-2.0 (MobileSAM code and weights, ChaoningZhang/MobileSAM).",
        "https://github.com/ChaoningZhang/MobileSAM",
        "Turns a box around a foreground object into an occlusion matte.",
        null, null, 100L * 1024 * 1024);

    public static IReadOnlyList<OnDeviceModelDescriptor> All { get; } =
        [PoseEstimator, SegmentationEncoder, SegmentationDecoder];

    /// <summary>
    /// Windows ML ships native runtimes for x64 and ARM64 only. On 32-bit x86 InkMotion still builds and runs, with the
    /// AI tracking and automatic matte options disabled.
    /// </summary>
    public static string? UnsupportedReason(Architecture architecture) => architecture switch
    {
        Architecture.X64 or Architecture.Arm64 => null,
        _ => "AI limb tracking and automatic mattes need the 64-bit (x64 or ARM64) edition of InkMotion, because " +
            "Windows ML is not available for 32-bit x86. Classical tracking modes and painted mattes still work.",
    };

    public static string? UnsupportedReason() => UnsupportedReason(RuntimeInformation.ProcessArchitecture);

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InkMotion", "Models");

    public static string ToHex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        return ToHex(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    /// <summary>
    /// ONNX files are protobuf ModelProto messages whose first field is ir_version (field 1, varint), so a valid
    /// export starts with byte 0x08. This rejects obviously wrong files before Windows ML loads them.
    /// </summary>
    public static bool LooksLikeOnnx(ReadOnlySpan<byte> header) => header.Length >= 2 && header[0] == 0x08;
}

/// <summary>Stores imported or downloaded models in a per-user folder. Nothing leaves the device.</summary>
public sealed class OnDeviceModelStore(string directory)
{
    public string Directory { get; } = directory;

    public static OnDeviceModelStore Default { get; } = new(OnDeviceModels.DefaultDirectory);

    public string GetPath(OnDeviceModelDescriptor model) => Path.Combine(Directory, model.FileName);

    public bool IsInstalled(OnDeviceModelDescriptor model) => File.Exists(GetPath(model));

    public bool AreInstalled(params OnDeviceModelDescriptor[] models) => models.All(IsInstalled);

    /// <summary>
    /// Copies a local ONNX file into the store. A published model must match its pinned SHA-256; before publication
    /// the file is accepted and its hash returned so the user can compare it with the exporter's output.
    /// </summary>
    public async Task<string> ImportAsync(OnDeviceModelDescriptor model, string sourcePath, CancellationToken cancellationToken)
    {
        var info = new FileInfo(sourcePath);
        if (!info.Exists) throw new FileNotFoundException("The model file could not be found.", sourcePath);
        if (info.Length is 0 || info.Length > model.MaximumBytes)
            throw new InvalidDataException($"{model.DisplayName} must be a non-empty ONNX file no larger than {model.MaximumBytes / (1024 * 1024)} MB.");
        await CheckHeaderAsync(sourcePath, cancellationToken);
        string hash = await OnDeviceModels.ComputeSha256Async(sourcePath, cancellationToken);
        if (model.Sha256 is { } expected && !string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"This file does not match the published {model.DisplayName} (SHA-256 {hash}).");
        System.IO.Directory.CreateDirectory(Directory);
        string temporary = GetPath(model) + ".importing";
        File.Copy(sourcePath, temporary, overwrite: true);
        File.Move(temporary, GetPath(model), overwrite: true);
        return hash;
    }

    /// <summary>
    /// Downloads a published model over HTTPS after explicit consent, verifying its size and SHA-256 before it
    /// replaces anything in the store. Never called automatically.
    /// </summary>
    public async Task DownloadAsync(OnDeviceModelDescriptor model, OnDeviceModelDownloadConsent consent,
        HttpClient client, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consent);
        if (consent.ModelId != model.Id)
            throw new InvalidOperationException("Download consent was given for a different model.");
        if (!model.IsPublished || !Uri.TryCreate(model.DownloadUrl, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"{model.DisplayName} has not been published for download yet. Export it with the scripts in build/models and import the file instead.");
        System.IO.Directory.CreateDirectory(Directory);
        string temporary = GetPath(model) + ".downloading";
        try
        {
            using HttpResponseMessage response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            long? length = response.Content.Headers.ContentLength;
            if (length > model.MaximumBytes) throw new InvalidDataException("The download is larger than expected.");
            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (FileStream target = File.Create(temporary))
            {
                byte[] buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > model.MaximumBytes) throw new InvalidDataException("The download is larger than expected.");
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    if (length > 0) progress?.Report(total / (double)length.Value);
                }
            }
            string hash = await OnDeviceModels.ComputeSha256Async(temporary, cancellationToken);
            if (!string.Equals(hash, model.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded file failed its SHA-256 check and was discarded.");
            await CheckHeaderAsync(temporary, cancellationToken);
            File.Move(temporary, GetPath(model), overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Remove(OnDeviceModelDescriptor model)
    {
        if (File.Exists(GetPath(model))) File.Delete(GetPath(model));
    }

    private static async Task CheckHeaderAsync(string path, CancellationToken cancellationToken)
    {
        byte[] header = new byte[2];
        await using FileStream stream = File.OpenRead(path);
        int read = await stream.ReadAsync(header, cancellationToken);
        if (!OnDeviceModels.LooksLikeOnnx(header.AsSpan(0, read)))
            throw new InvalidDataException("This is not an ONNX model file.");
    }
}
