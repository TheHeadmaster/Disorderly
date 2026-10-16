using Disorderly.Commands;
using Disorderly.Routing;
using Disorderly.Services;
using NUnit.Framework;

namespace Disorderly.Testing;

[TestFixture]
public class PaletteInteractionTests
{
    internal static CommandConfigurationStore Configuration(string name = "Example")
    {
        CommandConfigurationStore store = PaletteConfiguration.CreateStore();
        Publish(store, name);
        return store;
    }

    internal static void Publish(CommandConfigurationStore store, string name)
    {
        CommandDefinition command = new("example", name,
            [new ParameterDefinition("path", "Path", ParameterValueType.Path, true), new ParameterDefinition("text", "Text", ParameterValueType.Text, true)],
            "Command description", CompletionPolicy.Dismiss);
        BindingDestination.Command destination = new("example")
        {
            Metadata = new CommandBindingMetadata(new ArgumentSet([new ArgumentAssignment("path", new ArgumentValue.Path("Z:/unresolved"))]), CompletionPolicy.StayOpen),
        };
        CommandConfigurationValidationResult result = store.Publish(new RoutingConfiguration("root", [
            new("root", "Root", [new("dev", "root", new("D"), new BindingDestination.Context("development")), new("work", "root", new("W"), new BindingDestination.Context("work"))]),
            new("development", "Development", [new("servers-dev", "development", new("S"), new BindingDestination.Context("servers")), new("select", "development", new("C"), destination)]),
            new("work", "Work", [new("servers-work", "work", new("S"), new BindingDestination.Context("servers"))]),
            new("servers", "Servers", []),
        ]), [command]);
        Assert.That(result.IsValid, Is.True);
    }

    internal sealed class View : ICommandPaletteView
    {
        public event EventHandler<PaletteDismissEventArgs>? DismissRequested;
        public event EventHandler<PaletteGestureEventArgs>? GestureReceived;
        public event EventHandler<PaletteSubmissionEventArgs>? SubmissionRequested;
        public PalettePresentation Presentation { get; private set; } = null!;
        public int Shows { get; private set; }
        public bool FocusSucceeds { get; set; } = true;
        public void Render(PalettePresentation presentation) => this.Presentation = presentation;
        public bool ShowAtForegroundMonitor() { this.Shows++; return this.FocusSucceeds; }
        public void Hide() { }
        public void RequestRun(PaletteSubmissionEventArgs args) => this.SubmissionRequested?.Invoke(this, args);
        public void Dispose() { }
        public void Key(string key) => this.Send(this.Presentation.ActivationId, new(key));
        public void Send(Guid activation, LogicalGesture gesture) => this.GestureReceived?.Invoke(this, new(activation, gesture));
        public void LoseFocus(Guid? activation = null) => this.DismissRequested?.Invoke(this, new(activation ?? this.Presentation.ActivationId));
    }

    [TestCase("D", "Development")]
    [TestCase("W", "Work")]
    public void SharedContextsReturnThroughActualPathAndRootEscapeDismisses(string key, string name)
    {
        View view = new();
        using CommandPaletteController controller = new(view, Configuration());
        controller.Open();
        Guid activation = view.Presentation.ActivationId;
        view.Key(key); view.Key("S");
        Assert.That(view.Presentation.ContextName, Is.EqualTo("Servers"));
        Assert.That(view.Presentation.Bindings, Is.Empty);
        Assert.That(view.Presentation.Breadcrumb, Is.EqualTo($"Root / {name} / Servers"));
        view.Key("Escape");
        Assert.That(view.Presentation.ContextName, Is.EqualTo(name));
        Assert.That(view.Presentation.ActivationId, Is.EqualTo(activation));
        view.Key("Escape"); view.Key("Escape");
        Assert.That(controller.State, Is.EqualTo(PaletteState.Hidden));
        controller.Open();
        Assert.That(view.Presentation.ContextName, Is.EqualTo("Root"));
        Assert.That(view.Presentation.ActivationId, Is.Not.EqualTo(activation));
        Assert.That(view.Shows, Is.EqualTo(2));
    }

    [Test]
    public void NoMatchBackspaceAndParentBindingsPreserveNestedState()
    {
        View view = new();
        using CommandPaletteController controller = new(view, Configuration());
        controller.Open(); view.Key("D");
        PalettePresentation before = view.Presentation;
        view.Key("Backspace"); view.Key("W"); view.Key("X");
        Assert.That(view.Presentation, Is.SameAs(before));
        Assert.That(before.Bindings.Select(binding => binding.Name), Is.EqualTo(new[] { "Example", "Servers" }));
    }

    [Test]
    public void SelectionPreparesIncompleteInvocationWithPresetsWithoutNavigationOrAttempt()
    {
        View view = new();
        using CommandPaletteController controller = new(view, Configuration());
        controller.Open(); view.Key("D");
        string path = view.Presentation.Breadcrumb;
        view.Key("C");
        CommandInvocation invocation = view.Presentation.Selection!;
        Assert.That(invocation.Command.DisplayName, Is.EqualTo("Example"));
        Assert.That(invocation.Command.Description, Is.EqualTo("Command description"));
        Assert.That(invocation.Origin, Is.EqualTo(new InvocationOrigin.Binding("select", "development")));
        Assert.That(invocation.CompletionPolicy, Is.EqualTo(CompletionPolicy.StayOpen));
        Assert.That(invocation.Arguments.Assignments.Single().Value, Is.TypeOf<ArgumentValue.Path>());
        Assert.That(invocation.IsExecutionReady, Is.False);
        Assert.That(view.Presentation.SelectionStatus, Does.Contain("Execution is not yet available"));
        Assert.That(view.Presentation.Breadcrumb, Is.EqualTo(path));
        view.Key("C");
        Assert.That(view.Presentation.Selection!.Id, Is.Not.EqualTo(invocation.Id));
        view.Key("S");
        Assert.That(view.Presentation.Selection, Is.Null);
        Assert.That(controller.State, Is.EqualTo(PaletteState.Visible));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void NestedDismissalAndStaleEventsCannotAffectReplacement(bool focusLoss)
    {
        View view = new();
        using CommandPaletteController controller = new(view, Configuration());
        controller.Open(); view.Key("D");
        Guid old = view.Presentation.ActivationId;
        if (focusLoss) { view.LoseFocus(); } else { controller.Toggle(); }
        Assert.That(controller.State, Is.EqualTo(PaletteState.Hidden));
        view.Send(old, new("C"));
        controller.Open();
        view.Send(old, new("D")); view.LoseFocus(old);
        Assert.That(controller.State, Is.EqualTo(PaletteState.Visible));
        Assert.That(view.Presentation.ContextName, Is.EqualTo("Root"));
        Assert.That(view.Presentation.Selection, Is.Null);
    }

    [Test]
    public void ActiveSelectionAndNavigationRetainSnapshotUntilReopening()
    {
        View view = new();
        CommandConfigurationStore store = Configuration("Old");
        using CommandPaletteController controller = new(view, store);
        controller.Open(); view.Key("D");
        Publish(store, "New");
        view.Key("C");
        Assert.That(view.Presentation.Selection!.Command.DisplayName, Is.EqualTo("Old"));
        Assert.That(view.Presentation.Bindings[0].Name, Is.EqualTo("Old"));
        controller.Dismiss(); controller.Open(); view.Key("D"); view.Key("C");
        Assert.That(view.Presentation.Selection!.Command.DisplayName, Is.EqualTo("New"));
    }

    [Test]
    public void SameGestureRetainsOldDestinationUntilNextActivation()
    {
        View view = new();
        CommandConfigurationStore store = Configuration();
        using CommandPaletteController controller = new(view, store);
        controller.Open();
        Assert.That(store.Publish(new("root", [
            new("root", "Root", [new("changed", "root", new("D"), new BindingDestination.Command("ready"))])]),
            [new("ready", "Ready command", [])]).IsValid, Is.True);
        view.Key("D");
        Assert.That(view.Presentation.ContextName, Is.EqualTo("Development"));
        Assert.That(view.Presentation.Selection, Is.Null);
        controller.Dismiss(); controller.Open(); view.Key("D");
        Assert.That(view.Presentation.ContextName, Is.EqualTo("Root"));
        Assert.That(view.Presentation.Selection!.IsExecutionReady, Is.True);
        Assert.That(view.Presentation.Selection.Command.DisplayName, Is.EqualTo("Ready command"));
        Assert.That(view.Presentation.SelectionStatus, Does.Contain("Execution is not yet available"));
        Assert.That(controller.State, Is.EqualTo(PaletteState.Visible));
    }

    [Test]
    public void EmptyRootAndInvalidReplacementRemainUsable()
    {
        View view = new();
        CommandConfigurationStore store = PaletteConfiguration.CreateEmptyStore();
        Assert.That(store.Publish(new("missing", []), []).IsValid, Is.False);
        using CommandPaletteController controller = new(view, store);
        controller.Open();
        Assert.That(view.Presentation.ContextName, Is.EqualTo("Root"));
        Assert.That(view.Presentation.Bindings, Is.Empty);
        view.Key("X");
        Assert.That(controller.State, Is.EqualTo(PaletteState.Visible));
    }

    [Test]
    public void ActivationGestureIsReservedExactly()
    {
        CommandConfigurationStore store = PaletteConfiguration.CreateStore();
        Assert.That(store.Publish(new("root", [new("root", "Root", [new("binding", "root", PaletteConfiguration.ActivationGesture, new BindingDestination.Command("example"))])]),
            [new("example", "Example", [])]).IsValid, Is.False);
        Assert.That(store.Publish(new("root", [new("root", "Root", [new("binding", "root", new("Escape", GestureModifiers.Control), new BindingDestination.Command("example"))])]),
            [new("example", "Example", [])]).IsValid, Is.True);
    }
}