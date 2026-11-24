namespace InkMotion;

/// <summary>HDR delivery colour spaces. Projects without colour settings remain SDR Rec.709.</summary>
public enum ProjectColourSpace
{
    /// <summary>HDR10: Rec.2020 primaries, SMPTE ST 2084 (PQ) transfer, 10-bit.</summary>
    Hdr10Pq,
    /// <summary>Hybrid log-gamma: Rec.2020 primaries, ARIB STD-B67 transfer, 10-bit.</summary>
    Hlg,
}

/// <summary>
/// Explicit HDR colour management for a project. SDR artwork maps to <see cref="ReferenceWhiteNits"/>;
/// HDR sources keep highlights above it, up to <see cref="PeakNits"/>, which is also the mastering peak.
/// </summary>
public sealed record ProjectColourSettings(
    ProjectColourSpace Space = ProjectColourSpace.Hdr10Pq,
    double ReferenceWhiteNits = ProjectColourSettings.DefaultReferenceWhiteNits,
    double PeakNits = ProjectColourSettings.DefaultPeakNits)
{
    public const double DefaultReferenceWhiteNits = 203;
    public const double DefaultPeakNits = 1000;
    public const double MinReferenceWhiteNits = 80;
    public const double MaxReferenceWhiteNits = 400;
    public const double MinPeakNits = 400;
    public const double MaxPeakNits = 10000;

    public string DisplayName => Space == ProjectColourSpace.Hlg ? "HLG (Rec.2020)" : "HDR10 PQ (Rec.2020)";

    public void Validate()
    {
        if (!Enum.IsDefined(Space) ||
            !double.IsFinite(ReferenceWhiteNits) || ReferenceWhiteNits is < MinReferenceWhiteNits or > MaxReferenceWhiteNits ||
            !double.IsFinite(PeakNits) || PeakNits is < MinPeakNits or > MaxPeakNits ||
            PeakNits <= ReferenceWhiteNits)
        {
            throw new InvalidDataException(
                "HDR colour settings need reference white from 80 to 400 nits and a higher peak from 400 to 10,000 nits.");
        }
    }
}
