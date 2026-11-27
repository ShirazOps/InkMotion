using System.Runtime.InteropServices;
using System.Text.Json;
using InkMotion;
using Xunit;

namespace InkMotion.Tests;

public sealed class Hdr10PlusAnalyserTests
{
    private const int Width = 4, Height = 2;

    [Fact]
    public void SceneStatisticsAreLinearMaxRgbInTenthsOfANit()
    {
        ushort bright = Code(1000), dim = Code(100);
        // Eight pixels: one bright red, one bright blue, six dim grey.
        byte[] frame = Frame((bright, dim, 0), (0, 0, bright), (dim, dim, dim), (dim, dim, dim),
            (dim, dim, dim), (dim, dim, dim), (dim, dim, dim), (dim, dim, dim));
        var analyser = new Hdr10PlusAnalyser(Width, Height);
        analyser.Add(frame);
        analyser.Add(frame);

        JsonElement scene = WriteAndRead(analyser, 0, 2).GetProperty("SceneInfo")[1];
        JsonElement luminance = scene.GetProperty("LuminanceParameters");
        int brightMetadata = Hdr10PlusAnalyser.ToMetadata(Hdr10PlusAnalyser.CodeToNits(bright));
        int dimMetadata = Hdr10PlusAnalyser.ToMetadata(Hdr10PlusAnalyser.CodeToNits(dim));
        Assert.InRange(brightMetadata, 9_990, 10_010);
        Assert.Equal([brightMetadata, dimMetadata, brightMetadata],
            luminance.GetProperty("MaxScl").EnumerateArray().Select(v => v.GetInt32()));
        double average = (2 * Hdr10PlusAnalyser.CodeToNits(bright) + 6 * Hdr10PlusAnalyser.CodeToNits(dim)) / 8;
        Assert.Equal(Hdr10PlusAnalyser.ToMetadata(average), luminance.GetProperty("AverageRGB").GetInt32());
        // Nearest rank over 16 samples: percentiles up to 75 % fall on the dim pixels, 90 % and above on the bright ones.
        Assert.Equal([dimMetadata, dimMetadata, dimMetadata, dimMetadata, dimMetadata, dimMetadata,
                brightMetadata, brightMetadata, brightMetadata],
            luminance.GetProperty("LuminanceDistributions").GetProperty("DistributionValues").EnumerateArray()
                .Select(v => v.GetInt32()));
        Assert.Equal(0, scene.GetProperty("TargetedSystemDisplayMaximumLuminance").GetInt32());
        Assert.False(scene.TryGetProperty("BezierCurveData", out _));
        Assert.Equal(new Hdr10LightLevel((int)Math.Ceiling(Hdr10PlusAnalyser.CodeToNits(bright)),
            (int)Math.Ceiling(average)), analyser.LightLevel);
    }

    [Fact]
    public void AbruptDistributionChangeStartsANewSceneAndSlicesAreIndexedFromZero()
    {
        ushort darkCode = Code(50);
        byte[] dark = Uniform(darkCode), bright = Uniform(Code(800));
        // One pixel in eight changes, an L1 distance of 0.25: the same scene.
        byte[] slightlyBrighter = Frame([(Code(55), Code(55), Code(55)),
            .. Enumerable.Repeat((darkCode, darkCode, darkCode), Width * Height - 1)]);
        var analyser = new Hdr10PlusAnalyser(Width, Height);
        foreach (byte[] frame in new[] { dark, slightlyBrighter, bright, bright, dark })
            analyser.Add(frame);

        JsonElement root = WriteAndRead(analyser, 1, 5);
        JsonElement[] scenes = root.GetProperty("SceneInfo").EnumerateArray().ToArray();
        Assert.Equal([0, 1, 2, 3], scenes.Select(s => s.GetProperty("SequenceFrameIndex").GetInt32()));
        Assert.Equal([0, 1, 1, 2], scenes.Select(s => s.GetProperty("SceneId").GetInt32()));
        Assert.Equal([1, 0, 1, 0], scenes.Select(s => s.GetProperty("SceneFrameIndex").GetInt32()));
        Assert.Equal([0, 1, 3], root.GetProperty("SceneInfoSummary").GetProperty("SceneFirstFrameIndex")
            .EnumerateArray().Select(v => v.GetInt32()));
        // The first scene spans 50 and 55 nits, so its MaxSCL is the brighter frame's level.
        Assert.Equal(Hdr10PlusAnalyser.ToMetadata(Hdr10PlusAnalyser.CodeToNits(Code(55))),
            MaxScl(scenes[0])[0]);
        Assert.Equal(Hdr10PlusAnalyser.ToMetadata(Hdr10PlusAnalyser.CodeToNits(Code(800))), MaxScl(scenes[1])[0]);
        Assert.Equal(MaxScl(scenes[1]), MaxScl(scenes[2]));
        Assert.Equal(Hdr10PlusAnalyser.ToMetadata(Hdr10PlusAnalyser.CodeToNits(Code(50))), MaxScl(scenes[3])[0]);
    }

    [Fact]
    public void SavedAnalysisReproducesMetadataAndSignatures()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"inkmotion-hdr10plus-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var analyser = new Hdr10PlusAnalyser(Width, Height);
            foreach (ushort nits in new ushort[] { 10, 12, 900, 950 }) analyser.Add(Uniform(Code(nits)));
            string saved = Path.Combine(directory, "analysis.bin"), first = Path.Combine(directory, "a.json"),
                second = Path.Combine(directory, "b.json");
            analyser.Save(saved);
            analyser.Write(first, 0, 4);

            Hdr10PlusAnalyser loaded = Hdr10PlusAnalyser.Load(saved, Width, Height, 4);
            loaded.Write(second, 0, 4);

            Assert.Equal(File.ReadAllText(first), File.ReadAllText(second));
            Assert.Equal(analyser.Signatures, loaded.Signatures);
            Assert.Equal(analyser.LightLevel, loaded.LightLevel);
            Hdr10PlusMetadata.Validate(second, 4);
            Assert.Equal(analyser.Signatures[2], Hdr10PlusAnalyser.Sign(Uniform(Code(900)), Width, Height));
            Assert.NotEqual(analyser.Signatures[2], Hdr10PlusAnalyser.Sign(Uniform(Code(901)), Width, Height));
            Assert.Throws<InvalidDataException>(() => Hdr10PlusAnalyser.Load(saved, Width, Height, 5));
            Assert.Throws<InvalidDataException>(() => Hdr10PlusAnalyser.Load(saved, Width * 2, Height, 4));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ushort Code(double nits) => (ushort)Math.Round(HdrColour.PqEncode(nits) * 65535);

    private static int[] MaxScl(JsonElement scene) => scene.GetProperty("LuminanceParameters").GetProperty("MaxScl")
        .EnumerateArray().Select(v => v.GetInt32()).ToArray();

    private static byte[] Uniform(ushort code) =>
        Frame(Enumerable.Repeat(((ushort)code, (ushort)code, (ushort)code), Width * Height).ToArray());

    private static byte[] Frame(params (ushort R, ushort G, ushort B)[] pixels)
    {
        var values = new ushort[pixels.Length * 3];
        for (int i = 0; i < pixels.Length; i++)
            (values[i * 3], values[i * 3 + 1], values[i * 3 + 2]) = pixels[i];
        return MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
    }

    private static JsonElement WriteAndRead(Hdr10PlusAnalyser analyser, int start, int end)
    {
        string path = Path.Combine(Path.GetTempPath(), $"inkmotion-hdr10plus-{Guid.NewGuid():N}.json");
        try
        {
            analyser.Write(path, start, end);
            Hdr10PlusMetadata.Validate(path, end - start);
            return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
