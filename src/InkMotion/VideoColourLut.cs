using System.Globalization;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Windows.Graphics.DirectX;

namespace InkMotion;

public sealed record VideoColourLutData(int Size, byte[] RgbaBytes)
{
    public static VideoColourLutData Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(path), Path.GetFileName(path));
    }

    public static VideoColourLutData Parse(string content, string sourceName = "LUT")
    {
        ArgumentNullException.ThrowIfNull(content);
        int? size = null;
        List<(float Red, float Green, float Blue)> entries = [];
        double[] domainMin = [0, 0, 0];
        double[] domainMax = [1, 1, 1];

        foreach (string originalLine in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = originalLine.Split('#', 2)[0].Trim().TrimStart('\uFEFF');
            if (line.Length == 0)
            {
                continue;
            }

            string[] tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (tokens[0].ToUpperInvariant())
            {
                case "TITLE":
                    continue;
                case "LUT_1D_SIZE":
                    throw Invalid(sourceName, "1D LUTs are not supported; choose a 3D .cube LUT.");
                case "LUT_3D_SIZE":
                    if (tokens.Length != 2 || size is not null ||
                        !int.TryParse(tokens[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parsedSize) ||
                        parsedSize is < 2 or > 64)
                    {
                        throw Invalid(sourceName, "LUT_3D_SIZE must be declared once with a size from 2 through 64.");
                    }

                    size = parsedSize;
                    continue;
                case "DOMAIN_MIN":
                case "DOMAIN_MAX":
                    double[] domain = ParseTriple(tokens, sourceName, "domain");
                    if (tokens[0].Equals("DOMAIN_MIN", StringComparison.OrdinalIgnoreCase))
                    {
                        domainMin = domain;
                    }
                    else
                    {
                        domainMax = domain;
                    }
                    continue;
            }

            if (tokens.Length != 3 ||
                !float.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float red) ||
                !float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float green) ||
                !float.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float blue) ||
                !float.IsFinite(red) || !float.IsFinite(green) || !float.IsFinite(blue) ||
                red is < 0 or > 1 || green is < 0 or > 1 || blue is < 0 or > 1)
            {
                throw Invalid(sourceName, "Every LUT entry must contain three finite RGB values from 0 through 1.");
            }

            entries.Add((red, green, blue));
        }

        if (size is not int tableSize)
        {
            throw Invalid(sourceName, "LUT_3D_SIZE is missing.");
        }

        if (domainMin.Where((value, channel) => value != 0 || domainMax[channel] != 1).Any())
        {
            throw Invalid(sourceName, "Only the default DOMAIN_MIN 0 0 0 and DOMAIN_MAX 1 1 1 are supported.");
        }

        int expectedEntries = checked(tableSize * tableSize * tableSize);
        if (entries.Count != expectedEntries)
        {
            throw Invalid(sourceName, $"Expected {expectedEntries} LUT entries but found {entries.Count}.");
        }

        byte[] bytes = new byte[checked(expectedEntries * 4)];
        for (int blue = 0; blue < tableSize; blue++)
        {
            for (int green = 0; green < tableSize; green++)
            {
                for (int red = 0; red < tableSize; red++)
                {
                    int cubeIndex = ((blue * tableSize) + green) * tableSize + red;
                    int tableIndex = ((red * tableSize) + green) * tableSize + blue;
                    (float outputRed, float outputGreen, float outputBlue) = entries[cubeIndex];
                    int offset = tableIndex * 4;
                    bytes[offset] = ToByte(outputRed);
                    bytes[offset + 1] = ToByte(outputGreen);
                    bytes[offset + 2] = ToByte(outputBlue);
                    bytes[offset + 3] = byte.MaxValue;
                }
            }
        }

        return new VideoColourLutData(tableSize, bytes);
    }

    public EffectTransferTable3D CreateTransferTable(CanvasDevice device) =>
        EffectTransferTable3D.CreateFromBytes(
            device,
            RgbaBytes,
            Size,
            Size,
            Size,
            DirectXPixelFormat.R8G8B8A8UIntNormalized);

    private static double[] ParseTriple(string[] tokens, string sourceName, string name)
    {
        if (tokens.Length != 4 ||
            !double.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double first) ||
            !double.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double second) ||
            !double.TryParse(tokens[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double third) ||
            !double.IsFinite(first) || !double.IsFinite(second) || !double.IsFinite(third))
        {
            throw Invalid(sourceName, $"The {name} declaration must contain three finite values.");
        }

        return [first, second, third];
    }

    private static byte ToByte(float value) => (byte)Math.Round(value * byte.MaxValue);

    private static InvalidDataException Invalid(string sourceName, string details) =>
        new($"Invalid 3D LUT '{sourceName}': {details}");
}
