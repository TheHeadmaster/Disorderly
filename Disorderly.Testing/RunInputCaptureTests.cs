using Disorderly.Services;
using NUnit.Framework;

namespace Disorderly.Testing;

[TestFixture]
public class RunInputCaptureTests
{
    private static PaletteSubmissionEventArgs Request(Guid? activation = null, Guid? invocation = null, Guid? attempt = null, long revision = 0) =>
        new(activation ?? Guid.NewGuid(), invocation ?? Guid.NewGuid(), attempt, revision);

    [TestCase("activation")]
    [TestCase("invocation")]
    [TestCase("retry")]
    [TestCase("revision")]
    [TestCase("unavailable")]
    public void StateChangesRejectCapturedInputWithoutFallingBackToCurrent(string change)
    {
        PaletteSubmissionEventArgs original = Request();
        RunInputCapture capture = new();
        capture.Update(original, true); capture.Begin();
        PaletteSubmissionEventArgs next = change switch
        {
            "activation" => Request(invocation: original.InvocationId),
            "invocation" => Request(activation: original.ActivationId),
            "retry" => Request(original.ActivationId, original.InvocationId, Guid.NewGuid()),
            "revision" => Request(original.ActivationId, original.InvocationId, revision: 1),
            _ => original,
        };
        capture.Update(next, change != "unavailable");
        Assert.That(capture.HasCapturedInput, Is.False);
        Assert.That(capture.Take(), Is.Null, "An obsolete physical click is not a current automation click.");
        capture.Update(next, true); capture.Begin();
        Assert.That(capture.Take(), Is.SameAs(next));
    }

    [Test]
    public void MouseDownDismissReopenMouseUpCannotReuseCapturedSubmission()
    {
        RunInputCapture capture = new();
        capture.Update(Request(), true); capture.Begin(); capture.Clear();
        PaletteSubmissionEventArgs reopened = Request();
        capture.Update(reopened, true);
        Assert.That(capture.Take(), Is.Null);
        capture.Begin();
        Assert.That(capture.Take(), Is.SameAs(reopened));
    }

    [Test]
    public void FullyCanceledInputDoesNotBlockFreshKeyboardMouseOrAutomationActivation()
    {
        RunInputCapture capture = new();
        capture.Update(Request(), true); capture.Begin(); capture.Clear(); capture.Cancel();
        PaletteSubmissionEventArgs reopened = Request();
        capture.Update(reopened, true);
        Assert.That(capture.Take(), Is.SameAs(reopened), "Automation has no physical capture.");
        capture.Begin();
        Assert.That(capture.Take(), Is.SameAs(reopened));
        Assert.That(capture.HasCapturedInput, Is.False);
    }
}