using System.Numerics;
using Microsoft.ML.OnnxRuntime;

namespace InkMotion;

public sealed record PoseKeypoint(Vector2 Point, float Confidence);

/// <summary>
/// RTMPose top-down body pose estimation (COCO 17 keypoints) with SimCC outputs. Input "input" is
/// [1, 3, 256, 192] RGB normalised with ImageNet mean and standard deviation; outputs "simcc_x" [1, 17, 384] and
/// "simcc_y" [1, 17, 512] use a split ratio of 2.
/// </summary>
public sealed class RtmPoseModel : IDisposable
{
    public const int InputWidth = 192;
    public const int InputHeight = 256;
    public const int KeypointCount = 17;
    public const float SplitRatio = 2;
    public const float ConfidenceThreshold = 0.3f;
    private static readonly float[] Mean = [123.675f, 116.28f, 103.53f];
    private static readonly float[] Deviation = [58.395f, 57.12f, 57.375f];

    public static readonly string[] KeypointNames =
    [
        "nose", "left eye", "right eye", "left ear", "right ear", "left shoulder", "right shoulder",
        "left elbow", "right elbow", "left wrist", "right wrist", "left hip", "right hip",
        "left knee", "right knee", "left ankle", "right ankle",
    ];

    /// <summary>Body segments that can carry artwork: limbs plus the shoulder and hip lines and torso sides.</summary>
    public static readonly (int A, int B)[] Bones =
    [
        (5, 7), (7, 9), (6, 8), (8, 10), (11, 13), (13, 15), (12, 14), (14, 16),
        (5, 6), (11, 12), (5, 11), (6, 12), (3, 4),
    ];

    private readonly InferenceSession _session;

    public RtmPoseModel(string path)
    {
        _session = WindowsMlRuntime.CreateSession(path);
        WindowsMlRuntime.RequireContract(_session, "The pose model", ["input"], ["simcc_x", "simcc_y"]);
    }

    /// <summary>
    /// The crop RTMPose sees: the box is enlarged by 25 % and widened or heightened to the model's 3 : 4 aspect.
    /// </summary>
    public static (Vector2 Centre, Vector2 Size) CropFor(Vector2 centre, Vector2 size)
    {
        float width = size.X * 1.25f, height = size.Y * 1.25f;
        const float aspect = InputWidth / (float)InputHeight;
        if (width > height * aspect) height = width / aspect;
        else width = height * aspect;
        return (centre, new(Math.Max(1, width), Math.Max(1, height)));
    }

    /// <summary>Samples a BGRA frame into the normalised CHW tensor with bilinear filtering; outside pixels are black.</summary>
    public static float[] Preprocess(byte[] bgra, int width, int height, Vector2 centre, Vector2 cropSize)
    {
        var tensor = new float[3 * InputWidth * InputHeight];
        int plane = InputWidth * InputHeight;
        float stepX = cropSize.X / InputWidth, stepY = cropSize.Y / InputHeight;
        float left = centre.X - cropSize.X / 2, top = centre.Y - cropSize.Y / 2;
        Span<float> rgb = stackalloc float[3];
        for (int y = 0; y < InputHeight; y++)
        for (int x = 0; x < InputWidth; x++)
        {
            Sample(bgra, width, height, left + (x + 0.5f) * stepX - 0.5f, top + (y + 0.5f) * stepY - 0.5f, rgb);
            int index = y * InputWidth + x;
            for (int channel = 0; channel < 3; channel++)
                tensor[channel * plane + index] = (rgb[channel] - Mean[channel]) / Deviation[channel];
        }
        return tensor;
    }

    /// <summary>
    /// Decodes SimCC vectors: each coordinate is the arg-max bin divided by the split ratio, and confidence is the
    /// smaller of the two peak values (as in MMPose). Points are mapped back from the crop to frame pixels.
    /// </summary>
    public static PoseKeypoint[] Decode(ReadOnlySpan<float> simccX, ReadOnlySpan<float> simccY, Vector2 centre, Vector2 cropSize)
    {
        int binsX = (int)(InputWidth * SplitRatio), binsY = (int)(InputHeight * SplitRatio);
        if (simccX.Length != KeypointCount * binsX || simccY.Length != KeypointCount * binsY)
            throw new InvalidDataException("The pose model returned unexpected SimCC shapes.");
        var keypoints = new PoseKeypoint[KeypointCount];
        for (int k = 0; k < KeypointCount; k++)
        {
            (int bestX, float peakX) = ArgMax(simccX.Slice(k * binsX, binsX));
            (int bestY, float peakY) = ArgMax(simccY.Slice(k * binsY, binsY));
            float inputX = bestX / SplitRatio, inputY = bestY / SplitRatio;
            var point = new Vector2(
                centre.X + (inputX / InputWidth - 0.5f) * cropSize.X,
                centre.Y + (inputY / InputHeight - 0.5f) * cropSize.Y);
            keypoints[k] = new(point, Math.Min(peakX, peakY));
        }
        return keypoints;
    }

    /// <summary>Chooses the body segment whose joints best match a line the user drew, keeping A/B order.</summary>
    public static (int A, int B)? NearestBone(IReadOnlyList<PoseKeypoint> keypoints, Vector2 start, Vector2 end)
    {
        float length = Vector2.Distance(start, end);
        (int A, int B)? best = null;
        float bestDistance = float.MaxValue;
        foreach ((int a, int b) in Bones)
        {
            if (keypoints[a].Confidence < ConfidenceThreshold || keypoints[b].Confidence < ConfidenceThreshold) continue;
            float forward = Vector2.Distance(keypoints[a].Point, start) + Vector2.Distance(keypoints[b].Point, end);
            float reverse = Vector2.Distance(keypoints[b].Point, start) + Vector2.Distance(keypoints[a].Point, end);
            float distance = Math.Min(forward, reverse);
            if (distance < bestDistance && distance <= length * 0.9f)
            {
                bestDistance = distance;
                best = forward <= reverse ? (a, b) : (b, a);
            }
        }
        return best;
    }

    public PoseKeypoint[] Estimate(byte[] bgra, int width, int height, Vector2 centre, Vector2 size)
    {
        (Vector2 cropCentre, Vector2 cropSize) = CropFor(centre, size);
        float[] input = Preprocess(bgra, width, height, cropCentre, cropSize);
        using var results = _session.Run([WindowsMlRuntime.Tensor("input", input, 1, 3, InputHeight, InputWidth)]);
        float[] x = results.First(result => result.Name == "simcc_x").AsEnumerable<float>().ToArray();
        float[] y = results.First(result => result.Name == "simcc_y").AsEnumerable<float>().ToArray();
        return Decode(x, y, cropCentre, cropSize);
    }

    public void Dispose() => _session.Dispose();

    private static (int Index, float Value) ArgMax(ReadOnlySpan<float> values)
    {
        int best = 0;
        for (int i = 1; i < values.Length; i++) if (values[i] > values[best]) best = i;
        return (best, values[best]);
    }

    /// <summary>Bilinear BGRA sample returned as RGB in 0–255.</summary>
    internal static void Sample(byte[] bgra, int width, int height, float x, float y, Span<float> rgb)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        float fx = x - x0, fy = y - y0;
        rgb.Clear();
        for (int dy = 0; dy <= 1; dy++)
        for (int dx = 0; dx <= 1; dx++)
        {
            int px = x0 + dx, py = y0 + dy;
            if (px < 0 || py < 0 || px >= width || py >= height) continue;
            float weight = (dx == 0 ? 1 - fx : fx) * (dy == 0 ? 1 - fy : fy);
            int index = (py * width + px) * 4;
            rgb[0] += bgra[index + 2] * weight;
            rgb[1] += bgra[index + 1] * weight;
            rgb[2] += bgra[index] * weight;
        }
    }
}
