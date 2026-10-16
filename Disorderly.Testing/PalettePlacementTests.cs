using Disorderly.Utilities;
using NUnit.Framework;

namespace Disorderly.Testing;

[TestFixture]
public class PalettePlacementTests
{
    [TestCase(1)]
    [TestCase(1.5)]
    [TestCase(2)]
    public void VisiblePositioningAppliesOnlyFinalBounds(double scale)
    {
        PixelBounds work = new(-2560, -1440, 2560, 1400);
        List<PixelBounds> operations = [];
        List<string> steps = [];
        PalettePlacement.Position(true, work,
            () =>
            {
                steps.Add("dpi");
                Assert.That(operations, Is.Empty);
                return scale;
            },
            (width, maxHeight) =>
            {
                steps.Add("measure");
                Assert.That(width, Is.EqualTo(360));
                Assert.That(maxHeight, Is.GreaterThan(240));
                return 240;
            },
            bounds =>
            {
                steps.Add("place");
                operations.Add(bounds);
            });
        Assert.That(steps, Is.EqualTo(new[] { "dpi", "measure", "place" }));
        Assert.That(operations, Is.EqualTo(new[] { PalettePlacement.Calculate(work, 360, 240, scale, scale) }));
    }

    [Test]
    public void HiddenPreparationPrecedesDestinationDpiReadAndIsNotRepeatedWhenVisible()
    {
        PixelBounds work = new(1920, 0, 2560, 1400);
        List<PixelBounds> operations = [];
        double currentScale = 1;
        PalettePlacement.Position(false, work,
            () => currentScale,
            (_, _) => 240,
            bounds =>
            {
                operations.Add(bounds);
                currentScale = 1.5; // Native hidden relocation establishes destination DPI.
            });
        PixelBounds final = new(3916, 24, 540, 360);
        Assert.That(operations, Is.EqualTo(new[] { new PixelBounds(1920, 0, 1, 1), final }));
        operations.Clear();
        PalettePlacement.Position(true, work, () => currentScale, (_, _) => 240, operations.Add);
        Assert.That(operations, Is.EqualTo(new[] { final }));
    }

    [TestCase(300, 120, 1.5)]
    [TestCase(1, 1, 2)]
    public void VisiblePositioningClampsContentBeforeItsOnlyNativeOperation(int width, int height, double scale)
    {
        PixelBounds work = new(-1000, -500, width, height);
        List<PixelBounds> operations = [];
        PalettePlacement.Position(true, work, () => scale,
            (measuredWidth, maxHeight) =>
            {
                Assert.That(measuredWidth * scale, Is.LessThanOrEqualTo(width));
                Assert.That(maxHeight * scale, Is.LessThanOrEqualTo(height));
                return 1000;
            }, operations.Add);
        Assert.That(operations, Has.Count.EqualTo(1));
        PixelBounds result = operations[0];
        Assert.That(result.X, Is.GreaterThanOrEqualTo(work.X));
        Assert.That(result.Y, Is.GreaterThanOrEqualTo(work.Y));
        Assert.That(result.X + result.Width, Is.LessThanOrEqualTo(work.X + work.Width));
        Assert.That(result.Y + result.Height, Is.LessThanOrEqualTo(work.Y + work.Height));
    }

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