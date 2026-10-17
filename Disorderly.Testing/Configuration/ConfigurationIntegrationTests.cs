using Disorderly.Commands;
using Disorderly.Commands.Editing;
using Disorderly.Configuration;
using Disorderly.Operations;
using Disorderly.Services;
using Disorderly.Testing.Commands.Editing;
using Disorderly.Testing.Execution;
using Disorderly.Testing.Operations;
using NUnit.Framework;

namespace Disorderly.Testing.Configuration;

[TestFixture]
public class ConfigurationIntegrationTests
{
    [Test]
    public async Task SharedStoreReloadTakesEffectOnlyOnNextActivationAndNeverLaunchesOrStartsOperations()
    {
        ConfigurationFixture.Files files = new();
        ConfigurationService service = ConfigurationFixture.Service(files);
        PaletteInteractionTests.View view = new();
        ExecutionFixture.Launcher launcher = new();
        OperationFixture.Runner runner = new();
        OperationCoordinator operations = new(runner);
        using CommandPaletteController palette = new(view, service.Store, ExecutionFixture.Coordinator(launcher));
        palette.Open();
        Assert.That(view.Presentation.Bindings, Is.Empty);
        files.Content = ConfigurationCodec.Encode(ConfigurationFixture.Full("Old"));
        await service.ReloadAsync();
        Assert.That(view.Presentation.Bindings, Is.Empty);
        palette.Dismiss(); palette.Open();
        Assert.That(view.Presentation.Bindings.Length, Is.EqualTo(1));
        view.Send(view.Presentation.ActivationId, new("+", Disorderly.Routing.GestureModifiers.Control | Disorderly.Routing.GestureModifiers.Shift));
        view.Send(view.Presentation.ActivationId, new("Ω", Disorderly.Routing.GestureModifiers.Alt | Disorderly.Routing.GestureModifiers.Meta));
        CommandInvocation old = view.Presentation.Selection!;
        Assert.That(old.Command.DisplayName, Is.EqualTo("Old"));
        files.Content = ConfigurationCodec.Encode(ConfigurationFixture.Full("New"));
        await service.ReloadAsync();
        Assert.That(view.Presentation.Selection, Is.SameAs(old));
        Assert.That(view.Presentation.ContextName, Is.EqualTo("Child"));
        palette.Dismiss(); palette.Open();
        Assert.That(view.Presentation.ContextName, Is.EqualTo("Root"));
        Assert.That(service.Store.Current!.Catalog.Find("launch")!.DisplayName, Is.EqualTo("New"));
        Assert.That(launcher.Calls, Is.Empty);
        Assert.That(operations.Operations, Is.Empty);
        Assert.That(runner.Calls, Is.Empty);
    }

    [Test]
    public async Task CompatibleAndIncompatibleReloadPreserveRetainedDraftInterpretation()
    {
        ConfigurationDocument source = ConfigurationDocument.FromSnapshot(EditingFixture.Store().Current!);
        ConfigurationFixture.Files files = new() { Content = ConfigurationCodec.Encode(source) };
        ConfigurationService service = ConfigurationFixture.Service(files);
        await service.ReloadAsync();
        PaletteInteractionTests.View view = new();
        using CommandPaletteController palette = new(view, service.Store);
        palette.Open(); view.Key("D"); view.Key("N");
        PalettePresentation selected = view.Presentation;
        view.Edit(new(selected.ActivationId, selected.Selection!.Id, selected.Editing!.SessionId, selected.Editing.Revision, "count", new ArgumentEdit.RawText("-")));
        ConfigurationDocument renamed = source with { Commands = [source.Commands[0] with { DisplayName = "Renamed" }] };
        files.Content = ConfigurationCodec.Encode(renamed); await service.ReloadAsync();
        palette.Dismiss();
        InvocationDraft original = palette.DraftStore.Drafts.Single();
        palette.Open(); view.Key("Tab");
        Assert.That(view.Presentation.Selection!.Id, Is.EqualTo(original.Editing.Invocation.Id));
        Assert.That(view.Presentation.Selection.Catalog, Is.SameAs(original.Editing.Invocation.Catalog));
        Assert.That(view.Presentation.Editing!.Parameters[2].RawText, Is.EqualTo("-"));
        palette.Dismiss();
        files.Content = ConfigurationCodec.Encode(ConfigurationFixture.Empty); await service.ReloadAsync();
        palette.Open();
        Assert.That(view.Presentation.ResumeSuggestion, Is.Null);
        Assert.That(view.Presentation.DraftMessage, Does.Contain("changed"));
        Assert.That(palette.DraftStore.Drafts.Single().Id, Is.EqualTo(original.Id));
        Assert.That(palette.DraftStore.Drafts.Single().Editing.Invocation.Catalog, Is.SameAs(original.Editing.Invocation.Catalog));
    }

    [Test]
    public async Task ValidationUsesTargetStoreRulesWithoutPublishingOrWritingEarly()
    {
        ConfigurationFixture.Files files = new();
        Disorderly.Commands.CommandConfigurationStore store = new(new Disorderly.Routing.RoutingValidator([new("+", Disorderly.Routing.GestureModifiers.Control | Disorderly.Routing.GestureModifiers.Shift)]));
        store.Publish(ConfigurationFixture.Empty.Routing, []);
        ConfigurationService service = new(store, System.IO.Path.Combine(System.IO.Path.GetTempPath(), "unused.json"), files);
        object baseline = store.Current!;
        Assert.That(store.Validate(ConfigurationFixture.Full().Routing, ConfigurationFixture.Full().Commands).IsValid, Is.False);
        Assert.That((await service.SaveAsync(ConfigurationFixture.Full(), 0)).Status, Is.EqualTo(ConfigurationStatus.DomainInvalid));
        Assert.That(files.Writes, Is.Zero);
        Assert.That(store.Current, Is.SameAs(baseline));
    }
}