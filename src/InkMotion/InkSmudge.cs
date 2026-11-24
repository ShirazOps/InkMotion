namespace InkMotion;

internal static class InkSmudge
{
    public static void Apply(byte[] pixels, int pixelWidth, int pixelHeight, IReadOnlyList<InkPoint> stroke,
        float width, float height, float sizeScale)
    {
        InkPoint? previous = null;
        InkBrushStamping.Draw(stroke, width, height, sizeScale, 0.3, point =>
        {
            if (previous is null)
            {
                previous = point;
                return;
            }

            double dx = (point.X - previous.X) * width;
            double dy = (point.Y - previous.Y) * height;
            previous = point;
            if (Math.Abs(dx) + Math.Abs(dy) < 1e-8) return;
            double radius = TimelineCompositor.GetInkWidth(point) * sizeScale / 2;
            double centreX = point.X * width;
            double centreY = point.Y * height;
            int left = Math.Max(0, (int)Math.Floor(centreX - radius));
            int top = Math.Max(0, (int)Math.Floor(centreY - radius));
            int right = Math.Min(pixelWidth - 1, (int)Math.Ceiling(centreX + radius));
            int bottom = Math.Min(pixelHeight - 1, (int)Math.Ceiling(centreY + radius));
            int patchWidth = right - left + 1;
            int patchHeight = bottom - top + 1;
            if (patchWidth <= 0 || patchHeight <= 0) return;

            // Capture the translated source before writing any destination pixels: no read/write feedback.
            var source = new byte[checked(patchWidth * patchHeight * 4)];
            for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
            {
                int destination = ((y - top) * patchWidth + x - left) * 4;
                double sourceX = x - dx;
                double sourceY = y - dy;
                int x0 = (int)Math.Floor(sourceX);
                int y0 = (int)Math.Floor(sourceY);
                double tx = sourceX - x0;
                double ty = sourceY - y0;
                for (int channel = 0; channel < 4; channel++)
                {
                    double upper = Sample(x0, y0, channel) * (1 - tx) + Sample(x0 + 1, y0, channel) * tx;
                    double lower = Sample(x0, y0 + 1, channel) * (1 - tx) + Sample(x0 + 1, y0 + 1, channel) * tx;
                    source[destination + channel] = (byte)Math.Round(upper * (1 - ty) + lower * ty);
                }
            }

            for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
            {
                double distance = Math.Sqrt(Math.Pow(x + 0.5 - centreX, 2) + Math.Pow(y + 0.5 - centreY, 2));
                if (distance >= radius) continue;
                double strength = (0.3 + 0.5 * point.Pressure) * Math.Pow(1 - distance / radius, 2);
                int destination = (y * pixelWidth + x) * 4;
                int sample = ((y - top) * patchWidth + x - left) * 4;
                for (int channel = 0; channel < 4; channel++)
                {
                    pixels[destination + channel] = (byte)Math.Round(
                        pixels[destination + channel] * (1 - strength) + source[sample + channel] * strength);
                }
            }
        });

        byte Sample(int x, int y, int channel) =>
            x < 0 || x >= pixelWidth || y < 0 || y >= pixelHeight ? (byte)0 : pixels[(y * pixelWidth + x) * 4 + channel];
    }
}
