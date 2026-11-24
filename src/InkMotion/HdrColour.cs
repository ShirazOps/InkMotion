using System.Runtime.InteropServices;

namespace InkMotion;

/// <summary>Transfer characteristic of a decoded video source.</summary>
public enum VideoSourceTransfer { Sdr, Pq, Hlg }

/// <summary>
/// Colour-management maths for HDR projects.
/// The working space is extended-range, sign-preserving sRGB-encoded Rec.709, where 1.0 is SDR reference white:
/// SDR artwork composites exactly as before, HDR highlights exceed 1.0 and wider-gamut colours become negative.
/// </summary>
public static class HdrColour
{
    private const double PqM1 = 2610.0 / 16384;
    private const double PqM2 = 2523.0 / 4096 * 128;
    private const double PqC1 = 3424.0 / 4096;
    private const double PqC2 = 2413.0 / 4096 * 32;
    private const double PqC3 = 2392.0 / 4096 * 32;
    private const double HlgA = 0.17883277;
    private const double HlgB = 0.28466892;
    private const double HlgC = 0.55991073;
    private const double PreviewKnee = 0.8;
    private const int ChunkPixels = 8192;

    private static readonly double[] Rec709ToRec2020 =
    [
        0.627403895934699, 0.329283038377884, 0.043313065687417,
        0.069097289358232, 0.919540395075459, 0.011362315566309,
        0.016391438875150, 0.088013307877226, 0.895595253247624,
    ];

    private static readonly double[] Rec2020ToRec709 =
    [
        1.660491002108435, -0.587641138788550, -0.072849863319885,
        -0.124550474521591, 1.132899897125960, -0.008349422604369,
        -0.018150763354905, -0.100578898008007, 1.118729661362913,
    ];

    /// <summary>sRGB decode extended to all real values by mirroring negatives and continuing the curve above 1.</summary>
    public static double SrgbToLinear(double encoded)
    {
        double magnitude = Math.Abs(encoded);
        double linear = magnitude <= 0.04045 ? magnitude / 12.92 : Math.Pow((magnitude + 0.055) / 1.055, 2.4);
        return Math.CopySign(linear, encoded);
    }

    public static double LinearToSrgb(double linear)
    {
        double magnitude = Math.Abs(linear);
        double encoded = magnitude <= 0.0031308 ? magnitude * 12.92 : 1.055 * Math.Pow(magnitude, 1 / 2.4) - 0.055;
        return Math.CopySign(encoded, linear);
    }

    /// <summary>SMPTE ST 2084 inverse EOTF: absolute luminance in nits to a 0-1 signal.</summary>
    public static double PqEncode(double nits)
    {
        double y = Math.Clamp(nits / 10000, 0, 1);
        double power = Math.Pow(y, PqM1);
        return Math.Pow((PqC1 + PqC2 * power) / (1 + PqC3 * power), PqM2);
    }

    /// <summary>SMPTE ST 2084 EOTF: a 0-1 signal to absolute luminance in nits.</summary>
    public static double PqDecode(double signal)
    {
        double power = Math.Pow(Math.Clamp(signal, 0, 1), 1 / PqM2);
        return 10000 * Math.Pow(Math.Max(power - PqC1, 0) / (PqC2 - PqC3 * power), 1 / PqM1);
    }

    /// <summary>BT.2100 HLG OETF: normalised scene light to a 0-1 signal.</summary>
    public static double HlgOetf(double scene)
    {
        double e = Math.Clamp(scene, 0, 1);
        return e <= 1.0 / 12 ? Math.Sqrt(3 * e) : HlgA * Math.Log(12 * e - HlgB) + HlgC;
    }

    public static double HlgInverseOetf(double signal)
    {
        double e = Math.Clamp(signal, 0, 1);
        return e <= 0.5 ? e * e / 3 : (Math.Exp((e - HlgC) / HlgA) + HlgB) / 12;
    }

    /// <summary>BT.2100 HLG system gamma for a display of the given nominal peak.</summary>
    public static double HlgSystemGamma(double peakNits) => 1.2 + 0.42 * Math.Log10(peakNits / 1000);

    public static (double R, double G, double B) ConvertRec709ToRec2020(double r, double g, double b) =>
        Multiply(Rec709ToRec2020, r, g, b);

    public static (double R, double G, double B) ConvertRec2020ToRec709(double r, double g, double b) =>
        Multiply(Rec2020ToRec709, r, g, b);

    /// <summary>Converts one working-space pixel to a 0-1 HDR signal triplet in Rec.2020, clipping at the mastering peak.</summary>
    public static (double R, double G, double B) EncodePixel(double r, double g, double b, ProjectColourSettings settings)
    {
        double white = settings.ReferenceWhiteNits;
        (double r2, double g2, double b2) = ConvertRec709ToRec2020(
            SrgbToLinear(r) * white, SrgbToLinear(g) * white, SrgbToLinear(b) * white);
        r2 = Math.Clamp(r2, 0, settings.PeakNits);
        g2 = Math.Clamp(g2, 0, settings.PeakNits);
        b2 = Math.Clamp(b2, 0, settings.PeakNits);
        if (settings.Space == ProjectColourSpace.Hdr10Pq)
            return (PqEncode(r2), PqEncode(g2), PqEncode(b2));

        // HLG is scene-referred: invert the reference OOTF for a display of the project's peak luminance.
        double peak = settings.PeakNits;
        double gamma = HlgSystemGamma(peak);
        double displayLuma = (0.2627 * r2 + 0.6780 * g2 + 0.0593 * b2) / peak;
        if (displayLuma <= 0) return (0, 0, 0);
        double sceneScale = 1 / (peak * Math.Pow(displayLuma, (gamma - 1) / gamma));
        return (HlgOetf(r2 * sceneScale), HlgOetf(g2 * sceneScale), HlgOetf(b2 * sceneScale));
    }

    /// <summary>Converts a 0-1 PQ or HLG Rec.2020 signal triplet to working-space values.</summary>
    public static (double R, double G, double B) DecodePixel(double r, double g, double b, VideoSourceTransfer transfer,
        ProjectColourSettings settings)
    {
        double rn, gn, bn;
        if (transfer == VideoSourceTransfer.Pq)
        {
            (rn, gn, bn) = (PqDecode(r), PqDecode(g), PqDecode(b));
        }
        else if (transfer == VideoSourceTransfer.Hlg)
        {
            double peak = settings.PeakNits;
            double gamma = HlgSystemGamma(peak);
            (double rs, double gs, double bs) = (HlgInverseOetf(r), HlgInverseOetf(g), HlgInverseOetf(b));
            double sceneLuma = 0.2627 * rs + 0.6780 * gs + 0.0593 * bs;
            double scale = sceneLuma <= 0 ? 0 : peak * Math.Pow(sceneLuma, gamma - 1);
            (rn, gn, bn) = (rs * scale, gs * scale, bs * scale);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(transfer), "Only PQ and HLG sources need HDR decoding.");
        }

        double white = settings.ReferenceWhiteNits;
        (double r7, double g7, double b7) = ConvertRec2020ToRec709(rn / white, gn / white, bn / white);
        return (LinearToSrgb(r7), LinearToSrgb(g7), LinearToSrgb(b7));
    }

    /// <summary>
    /// SDR display approximation of a working-space pixel: highlights above 80% of reference white roll off
    /// smoothly towards it, preserving hue; out-of-gamut negatives are clipped.
    /// </summary>
    public static (double R, double G, double B) ToneMapForPreview(double r, double g, double b)
    {
        double lr = Math.Max(SrgbToLinear(r), 0);
        double lg = Math.Max(SrgbToLinear(g), 0);
        double lb = Math.Max(SrgbToLinear(b), 0);
        double peak = Math.Max(lr, Math.Max(lg, lb));
        if (peak > PreviewKnee)
        {
            double mapped = PreviewKnee + (1 - PreviewKnee) * (1 - Math.Exp(-(peak - PreviewKnee) / (1 - PreviewKnee)));
            double scale = mapped / peak;
            (lr, lg, lb) = (lr * scale, lg * scale, lb * scale);
        }
        return (LinearToSrgb(Math.Min(lr, 1)), LinearToSrgb(Math.Min(lg, 1)), LinearToSrgb(Math.Min(lb, 1)));
    }

    /// <summary>Converts premultiplied RGBA half-float working pixels to opaque rgb48le HDR signal for FFmpeg.</summary>
    public static byte[] EncodeWorkingFrame(byte[] rgbaHalf, ProjectColourSettings settings)
    {
        ArgumentNullException.ThrowIfNull(rgbaHalf);
        if (rgbaHalf.Length % 8 != 0) throw new ArgumentException("Half-float RGBA frames use eight bytes per pixel.", nameof(rgbaHalf));
        int pixels = rgbaHalf.Length / 8;
        byte[] output = new byte[checked(pixels * 6)];
        ForEachChunk(pixels, (start, end) =>
        {
            ReadOnlySpan<Half> source = MemoryMarshal.Cast<byte, Half>(rgbaHalf.AsSpan());
            Span<ushort> target = MemoryMarshal.Cast<byte, ushort>(output.AsSpan());
            for (int pixel = start; pixel < end; pixel++)
            {
                int index = pixel * 4;
                double alpha = (double)source[index + 3];
                double unpremultiply = alpha > 0 ? 1 / alpha : 0;
                (double r, double g, double b) = EncodePixel((double)source[index] * unpremultiply,
                    (double)source[index + 1] * unpremultiply, (double)source[index + 2] * unpremultiply, settings);
                // Matte transparency against black, as an opaque video frame would.
                double cover = Math.Clamp(alpha, 0, 1);
                target[pixel * 3] = ToUInt16(r * cover);
                target[pixel * 3 + 1] = ToUInt16(g * cover);
                target[pixel * 3 + 2] = ToUInt16(b * cover);
            }
        });
        return output;
    }

    /// <summary>Converts 8-bit BGRA SDR pixels, such as a test pattern, to rgb48le HDR signal at reference white.</summary>
    public static byte[] EncodeSdrFrame(byte[] bgra, ProjectColourSettings settings)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (bgra.Length % 4 != 0) throw new ArgumentException("BGRA frames use four bytes per pixel.", nameof(bgra));
        int pixels = bgra.Length / 4;
        byte[] output = new byte[checked(pixels * 6)];
        ForEachChunk(pixels, (start, end) =>
        {
            Span<ushort> target = MemoryMarshal.Cast<byte, ushort>(output.AsSpan());
            for (int pixel = start; pixel < end; pixel++)
            {
                int index = pixel * 4;
                (double r, double g, double b) = EncodePixel(bgra[index + 2] / 255.0, bgra[index + 1] / 255.0,
                    bgra[index] / 255.0, settings);
                target[pixel * 3] = ToUInt16(r);
                target[pixel * 3 + 1] = ToUInt16(g);
                target[pixel * 3 + 2] = ToUInt16(b);
            }
        });
        return output;
    }

    /// <summary>Converts rgb48le PQ/HLG Rec.2020 source pixels to opaque RGBA half-float working pixels.</summary>
    public static void DecodeToWorking(byte[] rgb48, byte[] rgbaHalf, VideoSourceTransfer transfer, ProjectColourSettings settings)
    {
        int pixels = ValidateDecode(rgb48, rgbaHalf, 8);
        ForEachChunk(pixels, (start, end) =>
        {
            ReadOnlySpan<ushort> source = MemoryMarshal.Cast<byte, ushort>(rgb48.AsSpan());
            Span<Half> target = MemoryMarshal.Cast<byte, Half>(rgbaHalf.AsSpan());
            for (int pixel = start; pixel < end; pixel++)
            {
                (double r, double g, double b) = DecodePixel(source[pixel * 3] / 65535.0, source[pixel * 3 + 1] / 65535.0,
                    source[pixel * 3 + 2] / 65535.0, transfer, settings);
                target[pixel * 4] = (Half)r;
                target[pixel * 4 + 1] = (Half)g;
                target[pixel * 4 + 2] = (Half)b;
                target[pixel * 4 + 3] = (Half)1;
            }
        });
    }

    /// <summary>Converts rgb48le PQ/HLG Rec.2020 source pixels to tone-mapped opaque 8-bit BGRA for SDR preview.</summary>
    public static void DecodeToPreview(byte[] rgb48, byte[] bgra, VideoSourceTransfer transfer, ProjectColourSettings settings)
    {
        int pixels = ValidateDecode(rgb48, bgra, 4);
        ForEachChunk(pixels, (start, end) =>
        {
            ReadOnlySpan<ushort> source = MemoryMarshal.Cast<byte, ushort>(rgb48.AsSpan());
            for (int pixel = start; pixel < end; pixel++)
            {
                (double r, double g, double b) = DecodePixel(source[pixel * 3] / 65535.0, source[pixel * 3 + 1] / 65535.0,
                    source[pixel * 3 + 2] / 65535.0, transfer, settings);
                (r, g, b) = ToneMapForPreview(r, g, b);
                int index = pixel * 4;
                bgra[index] = ToByte(b);
                bgra[index + 1] = ToByte(g);
                bgra[index + 2] = ToByte(r);
                bgra[index + 3] = 255;
            }
        });
    }

    private static int ValidateDecode(byte[] rgb48, byte[] output, int outputBytesPerPixel)
    {
        ArgumentNullException.ThrowIfNull(rgb48);
        ArgumentNullException.ThrowIfNull(output);
        if (rgb48.Length % 6 != 0 || output.Length != rgb48.Length / 6 * outputBytesPerPixel)
            throw new ArgumentException("Source and destination frames must contain the same number of pixels.", nameof(output));
        return rgb48.Length / 6;
    }

    private static void ForEachChunk(int pixels, Action<int, int> body)
    {
        int chunks = (pixels + ChunkPixels - 1) / ChunkPixels;
        Parallel.For(0, chunks, chunk => body(chunk * ChunkPixels, Math.Min(pixels, (chunk + 1) * ChunkPixels)));
    }

    private static (double, double, double) Multiply(double[] m, double r, double g, double b) =>
        (m[0] * r + m[1] * g + m[2] * b, m[3] * r + m[4] * g + m[5] * b, m[6] * r + m[7] * g + m[8] * b);

    private static ushort ToUInt16(double signal) => (ushort)Math.Round(Math.Clamp(signal, 0, 1) * 65535);

    private static byte ToByte(double signal) => (byte)Math.Round(Math.Clamp(signal, 0, 1) * 255);
}
