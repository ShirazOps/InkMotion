using System.Security.Cryptography;

namespace InkMotion;

public sealed record WhisperModelFiles(string Encoder, string Decoder, string Tokens);
public sealed record SpeechDiarisationModelFiles(string Segmentation, string Embedding);

public sealed class SpeechModelStore(string? directory = null)
{
    private const long MaximumModelBytes = 2L * 1024 * 1024 * 1024;
    public static SpeechModelStore Default { get; } = new();

    public string Directory { get; } = directory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InkMotion", "Models", "Speech");

    public WhisperModelFiles? Whisper => Existing("whisper-encoder.onnx") is string encoder &&
        Existing("whisper-decoder.onnx") is string decoder &&
        Existing("whisper-tokens.txt") is string tokens
            ? new(encoder, decoder, tokens)
            : null;

    public SpeechDiarisationModelFiles? Diarisation => Existing("speaker-segmentation.onnx") is string segmentation &&
        Existing("speaker-embedding.onnx") is string embedding
            ? new(segmentation, embedding)
            : null;

    public async Task<IReadOnlyList<string>> ImportWhisperFilesAsync(
        IReadOnlyList<string> sourceFiles, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        string? encoder = Find(sourceFiles, "tiny-encoder.int8.onnx");
        string? decoder = Find(sourceFiles, "tiny-decoder.int8.onnx");
        if (encoder is null || decoder is null)
        {
            encoder = Find(sourceFiles, "tiny-encoder.onnx");
            decoder = Find(sourceFiles, "tiny-decoder.onnx");
        }

        string tokens = Find(sourceFiles, "tiny-tokens.txt") ??
            throw new InvalidDataException("Select the Whisper encoder, decoder and tiny-tokens.txt files together.");
        if (encoder is null || decoder is null)
        {
            throw new InvalidDataException("Select a matching Whisper tiny encoder and decoder.");
        }

        await ValidateOnnxAsync(encoder, cancellationToken);
        await ValidateOnnxAsync(decoder, cancellationToken);
        await ValidateTextAsync(tokens, cancellationToken);
        System.IO.Directory.CreateDirectory(Directory);
        string encoderPath = Path.Combine(Directory, "whisper-encoder.onnx");
        string decoderPath = Path.Combine(Directory, "whisper-decoder.onnx");
        string tokensPath = Path.Combine(Directory, "whisper-tokens.txt");
        await CopySetAtomicallyAsync(
            [(encoder, encoderPath), (decoder, decoderPath), (tokens, tokensPath)], cancellationToken);
        return await HashFilesAsync([encoderPath, decoderPath, tokensPath], cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ImportDiarisationFilesAsync(
        IReadOnlyList<string> sourceFiles, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        string? segmentation = Find(sourceFiles, "model.int8.onnx") ?? Find(sourceFiles, "model.onnx");
        string embedding = Find(sourceFiles, "3dspeaker_speech_eres2net_sv_en_voxceleb_16k.onnx") ??
            throw new InvalidDataException("Select the pyannote segmentation model and English ERes2Net embedding model together.");
        if (segmentation is null)
        {
            throw new InvalidDataException("Select the pyannote segmentation model and English ERes2Net embedding model together.");
        }

        await ValidateOnnxAsync(segmentation, cancellationToken);
        await ValidateOnnxAsync(embedding, cancellationToken);
        System.IO.Directory.CreateDirectory(Directory);
        string segmentationPath = Path.Combine(Directory, "speaker-segmentation.onnx");
        string embeddingPath = Path.Combine(Directory, "speaker-embedding.onnx");
        await CopySetAtomicallyAsync(
            [(segmentation, segmentationPath), (embedding, embeddingPath)], cancellationToken);
        return await HashFilesAsync([segmentationPath, embeddingPath], cancellationToken);
    }

    private string? Existing(string fileName)
    {
        string path = Path.Combine(Directory, fileName);
        return File.Exists(path) ? path : null;
    }

    private static string? Find(IEnumerable<string> paths, string name) =>
        paths.FirstOrDefault(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase));

    private static async Task ValidateOnnxAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is < 4 or > MaximumModelBytes)
        {
            throw new InvalidDataException($"The model file is missing or has an invalid size: {Path.GetFileName(path)}.");
        }

        byte[] header = new byte[4];
        await using var stream = File.OpenRead(path);
        await stream.ReadExactlyAsync(header, cancellationToken);
        if (header[0] != 0x08)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is not an ONNX protobuf model.");
        }
    }

    private static async Task ValidateTextAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is < 1 or > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("The Whisper token file is missing or has an invalid size.");
        }

        await using var stream = File.OpenRead(path);
        var buffer = new byte[4096];
        if (await stream.ReadAsync(buffer, cancellationToken) == 0)
        {
            throw new InvalidDataException("The Whisper token file is empty.");
        }
    }

    private static async Task CopySetAtomicallyAsync(
        IReadOnlyList<(string Source, string Destination)> files, CancellationToken cancellationToken)
    {
        var staged = new List<(string Temporary, string Destination)>(files.Count);
        var backups = new List<(string Backup, string Destination)>();
        var installed = new List<string>();
        try
        {
            foreach ((string source, string destination) in files)
            {
                string temporary = destination + $".{Guid.NewGuid():N}.tmp";
                staged.Add((temporary, destination));
                await using var input = File.OpenRead(source);
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, cancellationToken);
            }

            foreach ((string temporary, string destination) in staged)
            {
                if (File.Exists(destination))
                {
                    string backup = destination + $".{Guid.NewGuid():N}.bak";
                    File.Move(destination, backup);
                    backups.Add((backup, destination));
                }

                File.Move(temporary, destination);
                installed.Add(destination);
            }
        }
        catch (Exception importError)
        {
            var rollbackErrors = new List<Exception>();
            foreach (string destination in installed.AsEnumerable().Reverse())
            {
                try
                {
                    File.Delete(destination);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    rollbackErrors.Add(ex);
                }
            }

            foreach ((string backup, string destination) in backups.AsEnumerable().Reverse())
            {
                try
                {
                    File.Move(backup, destination, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    rollbackErrors.Add(ex);
                }
            }

            if (rollbackErrors.Count > 0)
            {
                throw new AggregateException("Speech model import failed and the previous model set could not be fully restored.",
                    [importError, .. rollbackErrors]);
            }

            throw;
        }
        finally
        {
            foreach ((string temporary, _) in staged)
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        foreach ((string backup, _) in backups)
        {
            File.Delete(backup);
        }
    }

    private static async Task<IReadOnlyList<string>> HashFilesAsync(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var hashes = new List<string>();
        foreach (string path in paths)
        {
            await using var stream = File.OpenRead(path);
            hashes.Add($"{Path.GetFileName(path)}: {Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))}");
        }

        return hashes;
    }
}
