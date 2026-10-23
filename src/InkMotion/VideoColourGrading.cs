using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Windows.Graphics.DirectX;

namespace InkMotion;

public enum VideoColourCurveChannel
{
    Master,
    Red,
    Green,
    Blue,
}

public static class VideoColourGrading
{
    public const double MinimumExposure = -2;
    public const double MaximumExposure = 2;
    public const double MinimumContrast = -1;
    public const double MaximumContrast = 1;
    public const double MinimumTemperature = -1;
    public const double MaximumTemperature = 1;
    public const double MinimumTint = -1;
    public const double MaximumTint = 1;
    public const double MinimumSaturation = 0;
    public const double MaximumSaturation = 2;
    public const int MaximumCurvePoints = 32;
    public const int CurveTableSize = 256;

    public static bool IsNeutral(TimelineClipProject clip) =>
        IsNeutral(clip, VideoColourGradingKeyframes.GetStaticValue(clip));

    public static bool IsNeutral(TimelineClipProject clip, VideoColourGradingValue value) =>
        value.Exposure == 0 &&
        value.Contrast == 0 &&
        value.Temperature == 0 &&
        value.Tint == 0 &&
        value.Saturation == 1 &&
        string.IsNullOrWhiteSpace(clip.ColourLutPath) &&
        IsNeutralCurve(clip.MasterCurve) &&
        IsNeutralCurve(clip.RedCurve) &&
        IsNeutralCurve(clip.GreenCurve) &&
        IsNeutralCurve(clip.BlueCurve);

    public static List<VideoColourCurvePoint>? GetCurve(TimelineClipProject clip, VideoColourCurveChannel channel) =>
        channel switch
        {
            VideoColourCurveChannel.Master => clip.MasterCurve,
            VideoColourCurveChannel.Red => clip.RedCurve,
            VideoColourCurveChannel.Green => clip.GreenCurve,
            VideoColourCurveChannel.Blue => clip.BlueCurve,
            _ => throw new ArgumentOutOfRangeException(nameof(channel)),
        };

    public static void SetCurve(TimelineClipProject clip, VideoColourCurveChannel channel, List<VideoColourCurvePoint> curve)
    {
        ArgumentNullException.ThrowIfNull(curve);
        switch (channel)
        {
            case VideoColourCurveChannel.Master: clip.MasterCurve = curve; break;
            case VideoColourCurveChannel.Red: clip.RedCurve = curve; break;
            case VideoColourCurveChannel.Green: clip.GreenCurve = curve; break;
            case VideoColourCurveChannel.Blue: clip.BlueCurve = curve; break;
            default: throw new ArgumentOutOfRangeException(nameof(channel));
        }
    }

    public static float[] BuildTransferTable(IReadOnlyList<VideoColourCurvePoint> curve)
    {
        ArgumentNullException.ThrowIfNull(curve);
        var table = new float[CurveTableSize];
        for (int index = 0; index < table.Length; index++)
        {
            double input = index / (double)(table.Length - 1);
            table[index] = (float)Evaluate(curve, input);
        }

        return table;
    }

    internal static double Evaluate(IReadOnlyList<VideoColourCurvePoint> curve, double input)
    {
        int segment = 0;
        while (segment < curve.Count - 2 && input > curve[segment + 1].Input)
        {
            segment++;
        }

        VideoColourCurvePoint left = curve[segment];
        VideoColourCurvePoint right = curve[segment + 1];
        double amount = (input - left.Input) / (right.Input - left.Input);
        return Math.Clamp(left.Output + ((right.Output - left.Output) * amount), 0, 1);
    }

    public static bool IsNeutralCurve(IReadOnlyList<VideoColourCurvePoint>? curve) =>
        curve is { Count: >= 2 } &&
        curve[0] is { Input: 0, Output: 0 } &&
        curve[^1] is { Input: 1, Output: 1 } &&
        curve.All(point => point is not null && point.Input == point.Output);

    private static bool IsValidCurve(IReadOnlyList<VideoColourCurvePoint>? curve)
    {
        if (curve is not { Count: >= 2 and <= MaximumCurvePoints } ||
            curve[0] is null || curve[^1] is null ||
            curve[0].Input != 0 || curve[^1].Input != 1)
        {
            return false;
        }

        double previousInput = -1;
        foreach (VideoColourCurvePoint? point in curve)
        {
            if (point is null ||
                !double.IsFinite(point.Input) || point.Input is < 0 or > 1 ||
                !double.IsFinite(point.Output) || point.Output is < 0 or > 1 ||
                point.Input <= previousInput)
            {
                return false;
            }

            previousInput = point.Input;
        }

        return true;
    }

    public static string? Validate(TimelineClipProject clip, TimelineTrackKind kind)
    {
        bool valid = double.IsFinite(clip.Exposure) && clip.Exposure is >= MinimumExposure and <= MaximumExposure &&
            double.IsFinite(clip.Contrast) && clip.Contrast is >= MinimumContrast and <= MaximumContrast &&
            double.IsFinite(clip.WhiteBalanceTemperature) &&
            clip.WhiteBalanceTemperature is >= MinimumTemperature and <= MaximumTemperature &&
            double.IsFinite(clip.WhiteBalanceTint) && clip.WhiteBalanceTint is >= MinimumTint and <= MaximumTint &&
            double.IsFinite(clip.Saturation) && clip.Saturation is >= MinimumSaturation and <= MaximumSaturation &&
            IsValidCurve(clip.MasterCurve) && IsValidCurve(clip.RedCurve) &&
            IsValidCurve(clip.GreenCurve) && IsValidCurve(clip.BlueCurve);
        if (!valid)
        {
            return "Clip colour settings are outside their supported ranges.";
        }

        if (kind == TimelineTrackKind.Text && !IsNeutral(clip))
        {
            return "Colour grading is supported only for video and image clips.";
        }

        return null;
    }
}

public sealed class VideoColourGradingEffect : IDisposable
{
    private readonly ExposureEffect _exposure = new();
    private readonly ContrastEffect _contrast = new();
    private readonly TemperatureAndTintEffect _temperatureAndTint = new();
    private readonly SaturationEffect _saturation = new();
    private readonly TableTransferEffect _curves = new()
    {
        AlphaDisable = true,
    };
    private readonly TableTransfer3DEffect _lutEffect = new();
    private EffectTransferTable3D? _lutTable;
    private string? _lutPath;
    private VideoColourCurvePoint[]? _masterCurve;
    private VideoColourCurvePoint[]? _redCurve;
    private VideoColourCurvePoint[]? _greenCurve;
    private VideoColourCurvePoint[]? _blueCurve;

    public ICanvasImage GetOutput(
        CanvasBitmap source,
        TimelineClipProject clip,
        VideoColourGradingValue? animatedValue = null)
    {
        VideoColourGradingValue value = animatedValue ?? VideoColourGradingKeyframes.GetStaticValue(clip);
        _exposure.Source = source;
        _exposure.Exposure = (float)value.Exposure;
        _contrast.Source = _exposure;
        _contrast.Contrast = (float)value.Contrast;
        _temperatureAndTint.Source = _contrast;
        _temperatureAndTint.Temperature = (float)value.Temperature;
        _temperatureAndTint.Tint = (float)value.Tint;
        _saturation.Source = _temperatureAndTint;
        _saturation.Saturation = (float)value.Saturation;
        _curves.Source = _saturation;
        UpdateCurveTables(clip);
        if (string.IsNullOrWhiteSpace(clip.ColourLutPath))
        {
            return _curves;
        }

        if (!string.Equals(_lutPath, clip.ColourLutPath, StringComparison.OrdinalIgnoreCase))
        {
            _lutTable?.Dispose();
            VideoColourLutData data = VideoColourLutData.Load(clip.ColourLutPath);
            _lutTable = data.CreateTransferTable(source.Device);
            _lutPath = clip.ColourLutPath;
        }

        _lutEffect.Source = _curves;
        _lutEffect.Table = _lutTable;
        return _lutEffect;
    }

    private void UpdateCurveTables(TimelineClipProject clip)
    {
        if (CurveMatches(_masterCurve, clip.MasterCurve) &&
            CurveMatches(_redCurve, clip.RedCurve) &&
            CurveMatches(_greenCurve, clip.GreenCurve) &&
            CurveMatches(_blueCurve, clip.BlueCurve))
        {
            return;
        }

        _masterCurve = clip.MasterCurve.ToArray();
        _redCurve = clip.RedCurve.ToArray();
        _greenCurve = clip.GreenCurve.ToArray();
        _blueCurve = clip.BlueCurve.ToArray();
        _curves.RedTable = BuildChannelTable(_masterCurve, _redCurve);
        _curves.GreenTable = BuildChannelTable(_masterCurve, _greenCurve);
        _curves.BlueTable = BuildChannelTable(_masterCurve, _blueCurve);
    }

    private static float[] BuildChannelTable(
        IReadOnlyList<VideoColourCurvePoint> master,
        IReadOnlyList<VideoColourCurvePoint> channel)
    {
        float[] masterTable = VideoColourGrading.BuildTransferTable(master);
        var table = new float[masterTable.Length];
        for (int index = 0; index < table.Length; index++)
        {
            table[index] = (float)VideoColourGrading.Evaluate(channel, masterTable[index]);
        }

        return table;
    }

    private static bool CurveMatches(
        IReadOnlyList<VideoColourCurvePoint>? cached,
        IReadOnlyList<VideoColourCurvePoint> current) =>
        cached is not null && cached.SequenceEqual(current);

    public void Dispose()
    {
        _lutEffect.Dispose();
        _lutTable?.Dispose();
        _curves.Dispose();
        _saturation.Dispose();
        _temperatureAndTint.Dispose();
        _contrast.Dispose();
        _exposure.Dispose();
    }
}
