using Disorderly.Utilities;
using NUnit.Framework;

namespace Disorderly.Testing;

[TestFixture]
public class PalettePlacementTests
{
    [TestCase(0, 0, 1920, 1040, 1, 1544, 16, 360, 240)]
    [TestCase(1920, 0, 2560, 1400, 1.5, 3916, 24, 540, 360)]
    [TestCase(-2560, 0, 2560, 1400, 1.5, -564, 24, 540, 360)]
    [TestCase(0, -2160, 3840, 2080, 2, 3088, -2128, 720, 480)]
    [TestCase(60, 40, 1860, 1000, 1, 1544, 56, 360, 240)]
    public void AnchorsToWorkingAreaWithoutScalingItsOrigin(int x, int y, int width, int height, double scale, int expectedX, int expectedY, int expectedWidth, int expectedHeight)
    {
        PixelBounds result = PalettePlacement.Calculate(new PixelBounds(x, y, width, height), 360, 240, scale, scale);
        Assert.That(result, Is.EqualTo(new PixelBounds(expectedX, expectedY, expectedWidth, expectedHeight)));
    }

    [TestCase(300, 120)]
    [TestCase(1, 1)]
    [TestCase(720, 480)]
    public void OversizedPaletteStaysInsideWorkingArea(int width, int height)
    {
        PixelBounds work = new(-1000, -500, width, height);
        PixelBounds result = PalettePlacement.Calculate(work, 800, 900, 2, 2);
        Assert.That(result.X, Is.GreaterThanOrEqualTo(work.X));
        Assert.That(result.Y, Is.GreaterThanOrEqualTo(work.Y));
        Assert.That(result.X + result.Width, Is.LessThanOrEqualTo(work.X + work.Width));
        Assert.That(result.Y + result.Height, Is.LessThanOrEqualTo(work.Y + work.Height));
    }
}