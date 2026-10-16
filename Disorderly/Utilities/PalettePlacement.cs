namespace Disorderly.Utilities;

/// <summary>All coordinates and sizes in this record are physical screen pixels.</summary>
public readonly record struct PixelBounds(int X, int Y, int Width, int Height);

public static class PalettePlacement
{
    /// <summary>
    /// Prepares DPI only while hidden, then measures in DIPs and applies one final pixel rectangle.
    /// The callbacks keep the native operation sequence testable without foreground-window privileges.
    /// </summary>
    public static void Position(bool isVisible, PixelBounds work, Func<double> getScale, Func<double, double, double> measureHeight, Action<PixelBounds> place)
    {
        if (!isVisible)
        {
            // Restrict the temporary relocation to hidden HWNDs. Its DPI is read only afterward.
            place(new PixelBounds(work.X, work.Y, 1, 1));
        }

        double scale = getScale();
        PixelBounds available = Calculate(work, 360, work.Height / scale, scale, scale);
        double width = available.Width / scale;
        double maxHeight = available.Height / scale;
        double height = Math.Clamp(measureHeight(width, maxHeight), Math.Min(1, maxHeight), maxHeight);
        place(Calculate(work, width, height, scale, scale));
    }

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