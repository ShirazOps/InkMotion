namespace InkMotion;

public static class InkImageCutOut
{
    public static byte[] ApplyMaskToAlpha(byte[] bgra, int width, int height, IReadOnlyList<bool> foreground)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentNullException.ThrowIfNull(foreground);
        if (width <= 0 || height <= 0 || foreground.Count != checked(width * height) ||
            bgra.Length != checked(foreground.Count * 4))
        {
            throw new ArgumentException("The pixel buffer and foreground mask dimensions must match.");
        }

        byte[] result = bgra.ToArray();
        for (int pixel = 0; pixel < foreground.Count; pixel++)
        {
            if (!foreground[pixel])
            {
                result[(pixel * 4) + 3] = 0;
            }
        }

        return result;
    }
}
