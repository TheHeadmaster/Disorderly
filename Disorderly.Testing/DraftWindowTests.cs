using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Disorderly.Commands.Editing;
using Disorderly.Input;
using Disorderly.Services;
using Disorderly.Testing.Commands.Editing;
using Disorderly.View.Controls;
using Disorderly.View.Windows;
using NUnit.Framework;

namespace Disorderly.Testing;

[TestFixture]
[Apartment(ApartmentState.STA)]
public class DraftWindowTests
{
    private static PalettePresentation Root(InvocationDraft draft, Guid activation, bool browser = false) => new(activation, "Root", "Root", [], true, null,
        BrowsingDrafts: browser, Drafts: [new(draft.Id, draft.Generation, "Same name", "Unfinished input", "Root / Child")],
        ResumeSuggestion: browser ? null : new(draft.Id, draft.Generation, "Same name", "Unfinished input", "Root / Child"));

    private static InvocationDraft Draft()
    {
        ArgumentEditorSession editor = EditingFixture.Session();
        EditingFixture.Apply(editor, "count", new ArgumentEdit.RawText("-"));
        EditingFixture.Apply(editor, "count", new ArgumentEdit.Caret(1, 0));
        return new InvocationDraftStore().Retain(editor.Snapshot)!;
    }

    [Test]
    public void RootAndBrowserUseTheSameBoundedScrollAndIdentifiedAccessibleControls()
    {
        using CommandPaletteWindow window = new();
        InvocationDraft draft = Draft();
        Guid activation = Guid.NewGuid();
        window.Render(Root(draft, activation));
        DraftActionButton resume = (DraftActionButton)window.FindName("ResumeButton");
        Assert.That(((FrameworkElement)window.FindName("ResumePanel")).Visibility, Is.EqualTo(Visibility.Visible));
        Assert.That(resume.Action!.DraftId, Is.EqualTo(draft.Id));
        Assert.That(resume.Action.Generation, Is.EqualTo(draft.Generation));
        Assert.That(PaletteInputOwnership.IsInteractive(resume), Is.True);
        window.Render(Root(draft, activation, true));
        Assert.That(((FrameworkElement)window.FindName("DraftBrowser")).Visibility, Is.EqualTo(Visibility.Visible));
        Assert.That(((FrameworkElement)window.FindName("ResumePanel")).Visibility, Is.EqualTo(Visibility.Collapsed));
        StackPanel entry = (StackPanel)((StackPanel)window.FindName("DraftEntries")).Children[0];
        StackPanel actions = (StackPanel)entry.Children[^1];
        Assert.That(actions.Children.Count, Is.EqualTo(2));
        Assert.That(((DraftActionButton)actions.Children[1]).Action!.Action, Is.EqualTo(PaletteDraftAction.Discard));
        Assert.That(System.Windows.Automation.AutomationProperties.GetName(entry), Does.Contain(draft.Id.ToString()));
        Assert.That(((ScrollViewer)window.FindName("ContextScroll")).VerticalScrollBarVisibility, Is.EqualTo(ScrollBarVisibility.Auto));
        window.Render(Root(draft, activation, true) with { Drafts = [] });
        Assert.That(((FrameworkElement)window.FindName("DraftEmpty")).Visibility, Is.EqualTo(Visibility.Visible));
    }

    [Test]
    public void RestoredRawEditorsKeepTheirControlsAndPositionAcrossValidationRender()
    {
        InvocationDraft draft = Draft();
        ArgumentEditingSnapshot restored = ArgumentEditorSession.Restore(draft.Editing).Snapshot;
        using CommandPaletteWindow window = new();
        PalettePresentation presentation = new(Guid.NewGuid(), "Child", "Root / Child", [], false, restored.Invocation,
            Editing: restored, RestoreEditorFocus: true);
        window.Render(presentation);
        StackPanel editors = (StackPanel)window.FindName("ArgumentEditors");
        ArgumentParameterEditor integer = (ArgumentParameterEditor)editors.Children[2];
        Assert.That(integer.ValueInput.Text, Is.EqualTo("-"));
        integer.ValueInput.Select(1, 0);
        window.Render(presentation with { RestoreEditorFocus = false });
        Assert.That(editors.Children[2], Is.SameAs(integer));
        Assert.That(integer.ValueInput.SelectionStart, Is.EqualTo(1));
        Assert.That(((Button)window.FindName("RunButton")).IsEnabled, Is.False);
        Assert.That(restored.SessionId, Is.Not.EqualTo(draft.Editing.SessionId));
        window.Hide(); // Pending focus restoration must not revive a hidden editor.
        Assert.That(editors.Children, Is.Empty);
    }

    [Test]
    public void DraftButtonRejectsRetargetedPhysicalPressAndFreshInputRecovers()
    {
        DraftActionButton button = new();
        PaletteDraftEventArgs first = new(Guid.NewGuid(), PaletteDraftAction.Resume, Guid.NewGuid(), 1);
        button.Action = first;
        List<PaletteDraftEventArgs> actions = [];
        button.ActionRequested += (_, action) => actions.Add(action);
        MethodInfo begin = typeof(DraftActionButton).GetMethod("Begin", BindingFlags.NonPublic | BindingFlags.Instance)!;
        MethodInfo click = typeof(DraftActionButton).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!;
        begin.Invoke(button, null);
        button.Action = new(first.ActivationId, first.Action, Guid.NewGuid(), 2);
        click.Invoke(button, null);
        Assert.That(actions, Is.Empty);
        begin.Invoke(button, null);
        click.Invoke(button, null);
        Assert.That(actions.Single(), Is.SameAs(button.Action));
        // Automation without a physical press uses the current identified action.
        click.Invoke(button, null);
        Assert.That(actions.Count, Is.EqualTo(2));
    }

    [Test]
    public void OrdinaryRenderDoesNotInvalidateMatchingPhysicalActionAndHideChangesActivation()
    {
        DraftActionButton button = new();
        PaletteDraftEventArgs action = new(Guid.NewGuid(), PaletteDraftAction.Discard, Guid.NewGuid(), 1);
        button.Action = action;
        typeof(DraftActionButton).GetMethod("Begin", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(button, null);
        button.Action = new(action.ActivationId, action.Action, action.DraftId, action.Generation);
        Assert.That(button.Action, Is.SameAs(action));
        int accepted = 0;
        button.ActionRequested += (_, _) => accepted++;
        typeof(DraftActionButton).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(button, null);
        Assert.That(accepted, Is.EqualTo(1));
        Assert.That(PaletteInputOwnership.IsInteractive(button), Is.True);
    }
}