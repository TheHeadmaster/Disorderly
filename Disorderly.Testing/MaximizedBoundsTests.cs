using Disorderly.Utilities;
using NUnit.Framework;

namespace Disorderly.Testing;

[TestFixture]
public class MaximizedBoundsTests
{
    [TestCase(0, 0, 1920, 1080, 0, 0, 1920, 1040, 0, 0)]
    [TestCase(-2560, 0, 2560, 1440, -2500, 0, 2500, 1440, 60, 0)]
    [TestCase(0, -2160, 3840, 2160, 0, -2100, 3840, 2100, 0, 60)]
    [TestCase(1920, -400, 2560, 1440, 1920, -400, 2560, 1380, 0, 0)]
    public void MaximizeBoundsArePhysicalWorkAreaRelativeToMonitor(int x, int y, int width, int height,
        int workX, int workY, int workWidth, int workHeight, int offsetX, int offsetY)
    {
        PixelBounds bounds = MaximizedBounds.Calculate(new PixelBounds(x, y, width, height),
            new PixelBounds(workX, workY, workWidth, workHeight));
        Assert.That(bounds, Is.EqualTo(new PixelBounds(offsetX, offsetY, workWidth, workHeight)));
    }
}