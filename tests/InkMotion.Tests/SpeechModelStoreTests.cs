using Xunit;

namespace InkMotion.Tests;

public sealed class SpeechModelStoreTests
{
    [Fact]
    public async Task ImportWhisperFiles_ValidatesAndCopiesTheSelectedLocalFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), $"inkmotion-speech-models-{Guid.NewGuid():N}");
        string source = Path.Combine(root, "source");
        string storePath = Path.Combine(root, "store");
        Directory.CreateDirectory(source);
        try
        {
            string encoder = WriteModel(source, "tiny-encoder.int8.onnx");
            string decoder = WriteModel(source, "tiny-decoder.int8.onnx");
            string tokens = Path.Combine(source, "tiny-tokens.txt");
            await File.WriteAllTextAsync(tokens, "0\t<|endoftext|>\n1\t Hello\n");
            var store = new SpeechModelStore(storePath);

            IReadOnlyList<string> hashes = await store.ImportWhisperFilesAsync([tokens, decoder, encoder]);

            WhisperModelFiles imported = store.Whisper!;
            Assert.True(File.Exists(imported.Encoder));
            Assert.True(File.Exists(imported.Decoder));
            Assert.True(File.Exists(imported.Tokens));
            Assert.Equal(3, hashes.Count);
            Assert.All(hashes, hash => Assert.Contains(": ", hash));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImportDiarisationFiles_RequiresBothValidOnnxModels()
    {
        string root = Path.Combine(Path.GetTempPath(), $"inkmotion-speaker-models-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string segmentation = WriteModel(root, "model.int8.onnx");
            string embedding = WriteModel(root, "3dspeaker_speech_eres2net_sv_en_voxceleb_16k.onnx");
            var store = new SpeechModelStore(Path.Combine(root, "store"));

            IReadOnlyList<string> hashes = await store.ImportDiarisationFilesAsync([segmentation, embedding]);

            Assert.NotNull(store.Diarisation);
            Assert.Equal(2, hashes.Count);
            await File.WriteAllBytesAsync(embedding, [0x50, 0x4B, 0x03, 0x04]);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.ImportDiarisationFilesAsync([segmentation, embedding]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImportWhisperFiles_RestoresPreviousFilesWhenSetReplacementFails()
    {
        string root = Path.Combine(Path.GetTempPath(), $"inkmotion-speech-rollback-{Guid.NewGuid():N}");
        string source = Path.Combine(root, "source");
        string storePath = Path.Combine(root, "store");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(storePath);
        try
        {
            string encoder = WriteModel(source, "tiny-encoder.int8.onnx");
            string decoder = WriteModel(source, "tiny-decoder.int8.onnx");
            string tokens = Path.Combine(source, "tiny-tokens.txt");
            await File.WriteAllTextAsync(tokens, "new tokens");
            string installedEncoder = Path.Combine(storePath, "whisper-encoder.onnx");
            byte[] previousEncoder = [0x08, 0x07, 0x12, 0x00];
            await File.WriteAllBytesAsync(installedEncoder, previousEncoder);
            await File.WriteAllTextAsync(Path.Combine(storePath, "whisper-tokens.txt"), "old tokens");
            Directory.CreateDirectory(Path.Combine(storePath, "whisper-decoder.onnx"));
            var store = new SpeechModelStore(storePath);

            await Assert.ThrowsAsync<IOException>(() =>
                store.ImportWhisperFilesAsync([encoder, decoder, tokens]));

            Assert.Equal(previousEncoder, await File.ReadAllBytesAsync(installedEncoder));
            Assert.Equal("old tokens", await File.ReadAllTextAsync(Path.Combine(storePath, "whisper-tokens.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string WriteModel(string directory, string fileName)
    {
        string path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, [0x08, 0x07, 0x12, 0x00]);
        return path;
    }
}
