namespace Disorderly.Utilities;

/// <summary>All coordinates and sizes in this record are physical screen pixels.</summary>
public readonly record struct PixelBounds(int X, int Y, int Width, int Height);

public static class PalettePlacement
{
    /// <summary>Converts DIP dimensions/margin only; never scales the virtual desktop origin.</summary>
    public static PixelBounds Calculate(PixelBounds work, double widthDip, double heightDip, double scaleX, double scaleY, double marginDip = 16)
    {
        if (work.Width <= 0 || work.Height <= 0 || scaleX <= 0 || scaleY <= 0 || widthDip <= 0 || heightDip <= 0 || marginDip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(work));
        }

        int marginX = Math.Min((int)Math.Ceiling(marginDip * scaleX), (work.Width - 1) / 2);
        int marginY = Math.Min((int)Math.Ceiling(marginDip * scaleY), (work.Height - 1) / 2);
        int width = Math.Clamp((int)Math.Ceiling(widthDip * scaleX), 1, work.Width - (2 * marginX));
        int height = Math.Clamp((int)Math.Ceiling(heightDip * scaleY), 1, work.Height - (2 * marginY));
        return new PixelBounds(work.X + work.Width - marginX - width, work.Y + marginY, width, height);
    }
}