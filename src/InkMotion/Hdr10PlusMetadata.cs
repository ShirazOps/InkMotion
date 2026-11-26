using System.Buffers;
using System.Text.Json;

namespace InkMotion;

internal static class Hdr10PlusMetadata
{
    public static void Validate(string path, int expectedFrames)
    {
        if (expectedFrames <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedFrames), "HDR10+ metadata requires at least one output frame.");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var state = new JsonReaderState();
            int buffered = 0;
            int indexedFrames = 0;
            int sceneArrayDepth = -1;
            bool sceneInfoFound = false;
            bool sceneArrayOpen = false;
            bool sceneArrayClosed = false;
            bool expectSceneArray = false;
            bool expectFrameIndex = false;

            while (true)
            {
                int read = stream.Read(buffer, buffered, buffer.Length - buffered);
                bool finalBlock = read == 0;
                var reader = new Utf8JsonReader(buffer.AsSpan(0, buffered + read), finalBlock, state);

                while (reader.Read())
                {
                    if (expectSceneArray)
                    {
                        expectSceneArray = false;
                        if (reader.TokenType != JsonTokenType.StartArray)
                            throw new InvalidDataException("HDR10+ SceneInfo must be an array.");
                        sceneArrayOpen = true;
                        sceneArrayDepth = reader.CurrentDepth;
                        continue;
                    }

                    if (expectFrameIndex)
                    {
                        expectFrameIndex = false;
                        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int frameIndex) ||
                            frameIndex != indexedFrames || indexedFrames >= expectedFrames)
                        {
                            throw new InvalidDataException("HDR10+ SequenceFrameIndex values must match the export frames from zero without gaps.");
                        }
                        indexedFrames++;
                        continue;
                    }

                    if (reader.TokenType == JsonTokenType.PropertyName)
                    {
                        if (reader.CurrentDepth == 1 && reader.ValueTextEquals("SceneInfo"u8))
                        {
                            if (sceneInfoFound)
                                throw new InvalidDataException("HDR10+ metadata must contain exactly one SceneInfo array.");
                            sceneInfoFound = true;
                            expectSceneArray = true;
                        }
                        else if (sceneArrayOpen && reader.CurrentDepth == sceneArrayDepth + 2 &&
                                 reader.ValueTextEquals("SequenceFrameIndex"u8))
                        {
                            expectFrameIndex = true;
                        }
                    }
                    else if (reader.TokenType == JsonTokenType.EndArray &&
                             sceneArrayOpen && reader.CurrentDepth == sceneArrayDepth)
                    {
                        sceneArrayOpen = false;
                        sceneArrayClosed = true;
                    }
                }

                int consumed = checked((int)reader.BytesConsumed);
                state = reader.CurrentState;
                buffered += read - consumed;
                buffer.AsSpan(consumed, buffered).CopyTo(buffer);
                if (!finalBlock && buffered == buffer.Length)
                {
                    if (buffer.Length >= 16 * 1024 * 1024)
                        throw new InvalidDataException("HDR10+ metadata contains an oversized JSON token.");
                    byte[] expanded = ArrayPool<byte>.Shared.Rent(Math.Min(buffer.Length * 2, 16 * 1024 * 1024));
                    buffer.AsSpan(0, buffered).CopyTo(expanded);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = expanded;
                }
                if (finalBlock)
                    break;
            }

            if (expectSceneArray || expectFrameIndex || !sceneInfoFound || sceneArrayOpen || !sceneArrayClosed ||
                indexedFrames != expectedFrames)
            {
                throw new InvalidDataException($"HDR10+ metadata must contain exactly {expectedFrames} consecutive output-frame entries.");
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("HDR10+ metadata must be valid JSON.", ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
