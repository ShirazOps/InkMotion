namespace InkMotion;

public static class InkAlphaLock
{
    public static void Paint(byte[] destination, byte[] paint)
    {
        ValidateBuffers(destination, paint);
        for (int pixel = 0; pixel < destination.Length; pixel += 4)
        {
            int alpha = destination[pixel + 3], sourceAlpha = paint[pixel + 3];
            for (int channel = 0; channel < 3; channel++)
                destination[pixel + channel] = (byte)Math.Min(alpha,
                    (paint[pixel + channel] * alpha + destination[pixel + channel] * (255 - sourceAlpha) + 127) / 255);
        }
    }

    public static void RestoreAlpha(byte[] original, byte[] modified)
    {
        ValidateBuffers(original, modified);
        for (int pixel = 0; pixel < original.Length; pixel += 4)
        {
            int alpha = original[pixel + 3], changedAlpha = modified[pixel + 3];
            for (int channel = 0; channel < 3; channel++)
                modified[pixel + channel] = changedAlpha == 0 ? original[pixel + channel] :
                    (byte)Math.Min(alpha, (modified[pixel + channel] * alpha + changedAlpha / 2) / changedAlpha);
            modified[pixel + 3] = (byte)alpha;
        }
    }

    private static void ValidateBuffers(byte[] first, byte[] second)
    {
        if (first.Length != second.Length || first.Length % 4 != 0)
            throw new ArgumentException("Alpha lock needs equal premultiplied BGRA pixel buffers.");
    }
}
