namespace Disorderly.Utilities;

public static class MaximizedBounds
{
    /// <summary>WM_GETMINMAXINFO uses physical sizes and a position relative to the monitor origin.</summary>
    public static PixelBounds Calculate(PixelBounds monitor, PixelBounds work) =>
        new(work.X - monitor.X, work.Y - monitor.Y, work.Width, work.Height);
}