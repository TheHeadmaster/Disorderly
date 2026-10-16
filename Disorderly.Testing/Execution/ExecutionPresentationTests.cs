using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Disorderly.Commands;
using Disorderly.Execution;
using Disorderly.Services;
using Disorderly.View.Windows;
using NUnit.Framework;

namespace Disorderly.Testing.Execution;

[TestFixture]
[Apartment(ApartmentState.STA)]
public class ExecutionPresentationTests
{
    [TestCase(false, false, false)]
    [TestCase(true, true, false)]
    [TestCase(true, false, true)]
    public void RunAvailabilityReflectsArgumentsAndImplementation(bool configured, bool incomplete, bool expected)
    {
        using CommandPaletteWindow window = new();
        PaletteInteractionTests.View view = new();
        using CommandPaletteController controller = new(view, ExecutionFixture.Store(configured: configured, extraRequired: incomplete), ExecutionFixture.Coordinator(new()));
        controller.Open(); view.Key("D"); view.Key("N"); window.Render(view.Presentation);
        Button run = (Button)window.FindName("RunButton");
        Assert.That(run.IsEnabled, Is.EqualTo(expected));
        Assert.That(run.Focusable, Is.True);
        Assert.That(KeyboardNavigation.GetIsTabStop(run), Is.True);
        Assert.That(run.IsDefault, Is.False, "Enter is not a global palette submit gesture.");
    }

    [Test]
    public void PendingAndKnownFailureHaveAccessibleInlineFeedback()
    {
        using CommandPaletteWindow window = new();
        CommandInvocation invocation = ExecutionFixture.Invocation();
        ExecutionReadyRequest attempt = invocation.PrepareAttempt().Request!;
        PalettePresentation pending = new(Guid.NewGuid(), "Development", "Root / Development", [], false, invocation, true, true, null, attempt.AttemptId);
        window.Render(pending);
        Button run = (Button)window.FindName("RunButton");
        Assert.That(run.IsEnabled, Is.False);
        Assert.That(((TextBlock)window.FindName("SelectionStatus")).Text, Is.EqualTo("Launching..."));
        PalettePresentation failed = pending with
        {
            Submitting = false,
            Execution = ExecutionResult.For(attempt, ExecutionOutcome.FailedNotStarted, "NotFound", "Executable was not found."),
        };
        window.Render(failed);
        Assert.That(run.IsEnabled, Is.True);
        Assert.That(run.Content, Is.EqualTo("Retry"));
        Assert.That(((TextBlock)window.FindName("SelectionStatus")).Text, Does.Contain("not found"));
        Assert.That(System.Windows.Automation.AutomationProperties.GetName(run), Is.EqualTo("Retry selected command"));
    }

    [TestCase(Key.Enter, true)]
    [TestCase(Key.Space, true)]
    [TestCase(Key.Tab, true)]
    [TestCase(Key.Escape, false)]
    [TestCase(Key.N, false)]
    public void FocusedRunOwnsOnlyItsStandardActivationAndFocusKeys(Key key, bool expected)
    {
        Assert.That(CommandPaletteWindow.RunControlOwnsKey(key), Is.EqualTo(expected));
    }
}