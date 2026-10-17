using Disorderly.Commands;
using Disorderly.Configuration;
using Disorderly.Configuration.Editing;
using Disorderly.Routing;
using NUnit.Framework;

namespace Disorderly.Testing.Configuration;
[TestFixture]
public class ConfigurationEditorTests
{
    private static async Task<(ConfigurationService Service, ConfigurationEditorSession Editor, ConfigurationFixture.Files Files)> Create()
    {
        ConfigurationFixture.Files files = new()
        {
            Content = ConfigurationCodec.Encode(ConfigurationFixture.Full())
        };
        ConfigurationService service = ConfigurationFixture.Service(files);
        Assert.That((await service.ReloadAsync()).Success, Is.True);
        return (service, new(service), files);
    }

    [Test]
    public async Task RenamingPreservesIdentityReferencesAndActiveInteraction()
    {
        (ConfigurationService service, ConfigurationEditorSession editor, ConfigurationFixture.Files files) = await Create();
        CommandConfigurationSnapshot published = service.Store.Current!;
        CommandInteraction interaction = service.Store.CreateInteraction();
        CommandDefinition command = editor.Document.Commands.Single(item => item.Id == "launch");
        Assert.That(editor.UpdateCommand(editor.Token, "launch", command with { DisplayName = "Renamed" }), Is.True);
        Assert.That(service.Store.Current, Is.SameAs(published));
        Assert.That(interaction.Snapshot.Catalog.Find("launch")!.DisplayName, Is.Not.EqualTo("Renamed"));
        Assert.That(editor.Document.Routing.Contexts.Single(item => item.Id == "child").Bindings[0].Destination, Is.TypeOf<BindingDestination.Command>());
        Assert.That(editor.Document.Commands.Single(item => item.Id == "automatic"), Is.EqualTo(published.Catalog.Find("automatic")));
        Assert.That(files.Writes, Is.Zero);
        Assert.That((await editor.SaveAsync())!.Status, Is.EqualTo(ConfigurationStatus.Saved));
        Assert.That(editor.IsDirty, Is.False);
        Assert.That(editor.BaselineRevision, Is.EqualTo(service.Revision));
        Assert.That(service.Store.Current!.Catalog.Find("launch")!.DisplayName, Is.EqualTo("Renamed"));
        Assert.That(interaction.Snapshot, Is.SameAs(published));
    }

    [Test]
    public async Task CopiesCallerCollectionsAndRejectsIdentityReplacementAndStaleSelection()
    {
        (_, ConfigurationEditorSession editor, _) = await Create();
        List<ParameterDefinition> parameters = [new("value", "Value", ParameterValueType.Text)];
        ConfigurationEditToken old = editor.Token;
        Assert.That(editor.AddCommand(old, new("new", "New", parameters)), Is.True);
        parameters.Clear();
        Assert.That(editor.Document.Commands.Single(item => item.Id == "new").Parameters, Has.Count.EqualTo(1));
        CommandDefinition command = editor.Document.Commands.Single(item => item.Id == "new");
        Assert.That(editor.UpdateCommand(old, "new", command with { Id = "changed" }), Is.False);
        editor.Select("launch", null);
        Assert.That(editor.UpdateCommand(old, "new", command with { DisplayName = "Stale" }), Is.False);
        Assert.That(editor.SetInput(old, "field", "-", "Invalid"), Is.False);
    }

    [Test]
    public async Task ParameterOrderAndTypeChangesPreservePresetDataAndDiagnoseReferences()
    {
        (_, ConfigurationEditorSession editor, _) = await Create();
        CommandDefinition command = editor.Document.Commands.Single(item => item.Id == "launch");
        ArgumentSet original = ((BindingDestination.Command)editor.Document.Routing.Contexts.Single(item => item.Id == "child").Bindings[0].Destination).Metadata!.Presets!;
        editor.UpdateCommand(editor.Token, command.Id, command with { Parameters = command.Parameters.Reverse().ToArray() });
        Assert.That(editor.Document.Commands.Single(item => item.Id == command.Id).Parameters.Select(item => item.Id), Is.EqualTo(command.Parameters.Reverse().Select(item => item.Id)));
        editor.UpdateCommand(editor.Token, command.Id, command with { Parameters = command.Parameters.Select(item => item.Id == "executable" ? item with { ValueType = ParameterValueType.Text } : item).ToArray() });
        Assert.That(editor.CanSave, Is.False);
        Assert.That(editor.Diagnostics.Any(item => item.Command?.ParameterId == "executable"), Is.True);
        ArgumentSet retained = ((BindingDestination.Command)editor.Document.Routing.Contexts.Single(item => item.Id == "child").Bindings[0].Destination).Metadata!.Presets!;
        Assert.That(retained.Assignments, Is.EqualTo(original.Assignments));
    }

    [TestCase(ParameterValueType.Text, "", typeof(ArgumentValue.Text))]
    [TestCase(ParameterValueType.Boolean, "False", typeof(ArgumentValue.Boolean))]
    [TestCase(ParameterValueType.Integer, "0", typeof(ArgumentValue.Integer))]
    [TestCase(ParameterValueType.Integer, "-9223372036854775808", typeof(ArgumentValue.Integer))]
    [TestCase(ParameterValueType.Integer, "9223372036854775807", typeof(ArgumentValue.Integer))]
    [TestCase(ParameterValueType.Path, @"Z:\opaque\missing.exe", typeof(ArgumentValue.Path))]
    public async Task PresetsPreserveTypedPresenceAndRoundTrip(ParameterValueType type, string raw, Type expected)
    {
        (_, ConfigurationEditorSession editor, ConfigurationFixture.Files files) = await Create();
        string parameter = type switch
        {
            ParameterValueType.Text => "text",
            ParameterValueType.Boolean => "flag",
            ParameterValueType.Integer => "count",
            _ => "executable"
        };
        Assert.That(editor.SetPresetInput(editor.Token, "child", "run", parameter, true, raw), Is.True);
        Assert.That((await editor.SaveAsync())!.Success, Is.True);
        ConfigurationDocument document = ConfigurationCodec.Decode(files.Content!).Document!;
        ArgumentSet values = ((BindingDestination.Command)document.Routing.Contexts.Single(item => item.Id == "child").Bindings[0].Destination).Metadata!.Presets!;
        Assert.That(values.Assignments.Single(item => item.ParameterId == parameter).Value.GetType(), Is.EqualTo(expected));
        Assert.That(values.Assignments.Single(item => item.ParameterId == "text").Value, Is.EqualTo(new ArgumentValue.Text("")));
        Assert.That(values.Assignments.Single(item => item.ParameterId == "flag").Value, Is.EqualTo(new ArgumentValue.Boolean(false)));
    }

    [TestCase("-")]
    [TestCase("1.5")]
    [TestCase("9223372036854775808")]
    [TestCase("no")]
    public async Task InvalidIntegerBufferNeverSubmitsOldValidValue(string raw)
    {
        (_, ConfigurationEditorSession editor, ConfigurationFixture.Files files) = await Create();
        editor.SetPresetInput(editor.Token, "child", "run", "count", true, raw);
        Assert.That(editor.Input("binding/run/count/Integer")!.Raw, Is.EqualTo(raw));
        Assert.That(editor.CanSave, Is.False);
        Assert.That(await editor.SaveAsync(), Is.Null);
        Assert.That(files.Writes, Is.Zero);
        editor.SetPresetInput(editor.Token, "child", "run", "count", true, "-2");
        Assert.That(editor.CanSave, Is.True);
        editor.SetPresetInput(editor.Token, "child", "run", "count", false, "");
        ArgumentSet values = ((BindingDestination.Command)editor.Document.Routing.Contexts.Single(item => item.Id == "child").Bindings[0].Destination).Metadata!.Presets!;
        Assert.That(values.Assignments.Any(item => item.ParameterId == "count"), Is.False);
    }

    [Test]
    public async Task ContextReuseAndBindingOwnershipArePreserved()
    {
        (_, ConfigurationEditorSession editor, _) = await Create();
        editor.AddContext(editor.Token, new("other", "Other", []));
        editor.PutBinding(editor.Token, "other", new("shared", "other", new("S"), new BindingDestination.Context("child")), true);
        Assert.That(editor.PutBinding(editor.Token, "root", new("bad-owner", "other", new("A"), new BindingDestination.Context("child")), true), Is.False);
        editor.RenameContext(editor.Token, "child", "Shared");
        Assert.That(editor.Document.Routing.Contexts.Count(item => item.Id == "child"), Is.EqualTo(1));
        Assert.That(editor.Document.Routing.Contexts.Single(item => item.Id == "child").DisplayName, Is.EqualTo("Shared"));
        Assert.That(editor.Diagnostics.Any(item => item.Warning), Is.True);
        Assert.That(editor.CanSave, Is.True);
        Assert.That(editor.RemoveNewContext(editor.Token, "child"), Is.False);
        Assert.That(editor.RemoveNewContext(editor.Token, "root"), Is.False);
    }

    [Test]
    public async Task DomainValidatorDiagnosesCycleDuplicateAndReservedGestures()
    {
        (_, ConfigurationEditorSession editor, _) = await Create();
        editor.PutBinding(editor.Token, "child", new("cycle", "child", new("X"), new BindingDestination.Context("root")), true);
        Assert.That(editor.CanSave, Is.False);
        editor.RemoveBinding(editor.Token, "child", "cycle");
        editor.PutBinding(editor.Token, "child", new("reserved", "child", new("D", GestureModifiers.Control | GestureModifiers.Shift), new BindingDestination.Command("launch")), true);
        Assert.That(editor.CanSave, Is.False);
        editor.RemoveBinding(editor.Token, "child", "reserved");
        editor.PutBinding(editor.Token, "child", new("duplicate", "child", new("Ω", GestureModifiers.Alt | GestureModifiers.Meta), new BindingDestination.Command("launch")), true);
        Assert.That(editor.CanSave, Is.False);
        editor.RemoveBinding(editor.Token, "child", "duplicate");
        Assert.That(editor.CanSave, Is.True);
    }

    [Test]
    public async Task MissingFileAllowsFirstConfigurationAndExplicitDiscardProtectsWork()
    {
        ConfigurationFixture.Files files = new();
        ConfigurationService service = ConfigurationFixture.Service(files);
        Assert.That((await service.ReloadAsync()).Status, Is.EqualTo(ConfigurationStatus.Missing));
        ConfigurationEditorSession editor = new(service);
        editor.AddCommand(editor.Token, new("first", "First", []));
        Assert.That(editor.AdoptPublished(false), Is.False);
        Assert.That(editor.IsDirty, Is.True);
        Assert.That((await editor.SaveAsync())!.Success, Is.True);
        editor.AddContext(editor.Token, new("unused", "Unused", []));
        Assert.That(editor.AdoptPublished(true), Is.True);
        Assert.That(editor.Document.Routing.Contexts, Has.Count.EqualTo(1));
        Assert.That(editor.Document.Commands.Single().Id, Is.EqualTo("first"));
    }

    [TestCase(ConfigurationWriteStatus.Failed, false)]
    [TestCase(ConfigurationWriteStatus.Uncertain, null)]
    [TestCase(ConfigurationWriteStatus.Conflict, false)]
    public async Task FailedSaveRetainsExactProposal(ConfigurationWriteStatus status, bool? replaced)
    {
        (_, ConfigurationEditorSession editor, ConfigurationFixture.Files files) = await Create();
        editor.AddCommand(editor.Token, new("new", "New", []));
        ConfigurationDocument proposal = editor.Document;
        files.WriteResult = new(status, replaced, "Injected");
        Assert.That((await editor.SaveAsync())!.Success, Is.False);
        Assert.That(editor.IsDirty, Is.True);
        Assert.That(editor.Document, Is.SameAs(proposal));
    }

    [Test]
    public async Task ReloadRetainsDirtyCopyAndStaleSaveCannotWrite()
    {
        (ConfigurationService service, ConfigurationEditorSession editor, ConfigurationFixture.Files files) = await Create();
        editor.AddCommand(editor.Token, new("private", "Private", []));
        files.Content = ConfigurationCodec.Encode(ConfigurationFixture.Full("External"));
        await service.ReloadAsync();
        editor.RefreshPublicationStatus();
        Assert.That(editor.IsStale, Is.True);
        Assert.That(editor.Document.Commands.Any(item => item.Id == "private"), Is.True);
        Assert.That(await editor.SaveAsync(), Is.Null);
        Assert.That(files.Writes, Is.Zero);
        Assert.That(editor.PublishedSummary, Does.Contain("External"));
        editor.AdoptPublished(true);
        Assert.That(editor.IsStale, Is.False);
    }

    [Test]
    public async Task PendingSaveFreezesEditsAndRejectsDuplicateSubmission()
    {
        (_, ConfigurationEditorSession editor, ConfigurationFixture.Files files) = await Create();
        editor.AddCommand(editor.Token, new("new", "New", []));
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        files.BeforeWrite = () => release.Task;
        Task<ConfigurationResult?> pending = editor.SaveAsync();
        Assert.That(editor.IsSaving, Is.True);
        Assert.That(editor.AddContext(editor.Token, new("racing", "Racing", [])), Is.False);
        Assert.That(await editor.SaveAsync(), Is.Null);
        release.SetResult();
        Assert.That((await pending)!.Success, Is.True);
        Assert.That(files.Writes, Is.EqualTo(1));
    }

    [Test]
    public async Task SavedButUnpublishedRetainsProposalAndMarksReconciliation()
    {
        (ConfigurationService service, ConfigurationEditorSession editor, ConfigurationFixture.Files files) = await Create();
        editor.AddCommand(editor.Token, new("new", "New", []));
        files.BeforeWrite = () =>
        {
            ConfigurationDocument external = ConfigurationFixture.Full("Concurrent publication");
            service.Store.Publish(external.Routing, external.Commands);
            return Task.CompletedTask;
        };
        ConfigurationResult result = (await editor.SaveAsync())!;
        Assert.That(result.Status, Is.EqualTo(ConfigurationStatus.Conflict));
        Assert.That(result.Replaced, Is.True);
        Assert.That(editor.IsDirty, Is.True);
        Assert.That(editor.Status, Does.Contain("not published"));
    }

    [Test]
    public async Task FirstCommandContextAndBindingSaveAndReloadAsOneConfiguration()
    {
        ConfigurationFixture.Files files = new();
        ConfigurationService service = ConfigurationFixture.Service(files);
        ConfigurationEditorSession editor = new(service);
        editor.AddCommand(editor.Token, new("new", "New", [new("path", "Path", ParameterValueType.Path, true)], DefaultCompletionPolicy: CompletionPolicy.StayOpen, Icon: new CommandIcon.BuiltIn("application"), Execution: new CommandExecutionSpecification.LaunchExecutable("path")));
        editor.AddContext(editor.Token, new("apps", "Applications", []));
        editor.PutBinding(editor.Token, "root", new("enter", "root", new("A"), new BindingDestination.Context("apps")), true);
        editor.PutBinding(editor.Token, "apps", new("run", "apps", new("N"), new BindingDestination.Command("new") { Metadata = new(CompletionOverride: CompletionPolicy.Dismiss) }), true);
        editor.SetPresetInput(editor.Token, "apps", "run", "path", true, @"Z:\not-required-to-exist.exe");
        Assert.That((await editor.SaveAsync())!.Success, Is.True);
        Assert.That((await service.ReloadAsync()).Success, Is.True);
        CommandInteraction interaction = service.Store.CreateInteraction();
        interaction.Routing.Route(new("A"));
        CommandInvocation invocation = interaction.Prepare((RoutingResult.CommandRequested)interaction.Routing.Route(new("N"))).Invocation!;
        Assert.That(invocation.Command.Id, Is.EqualTo("new"));
        Assert.That(invocation.CompletionPolicy, Is.EqualTo(CompletionPolicy.Dismiss));
        Assert.That(invocation.IsExecutionReady, Is.True);
    }

    [Test]
    public async Task ExternalContentConflictAndInvalidFileCannotBeOverwrittenFromFallback()
    {
        (ConfigurationService service, ConfigurationEditorSession editor, ConfigurationFixture.Files files) = await Create();
        editor.AddCommand(editor.Token, new("new", "New", []));
        files.Content = ConfigurationFixture.Bytes("{broken");
        Assert.That((await editor.SaveAsync())!.Status, Is.EqualTo(ConfigurationStatus.Conflict));
        Assert.That(editor.IsDirty, Is.True);
        Assert.That(files.Content, Is.EqualTo(ConfigurationFixture.Bytes("{broken")));
        Assert.That((await service.ReloadAsync()).Status, Is.EqualTo(ConfigurationStatus.InvalidDocument));
        editor.AdoptPublished(true);
        editor.AddCommand(editor.Token, new("another", "Another", []));
        Assert.That((await editor.SaveAsync())!.Status, Is.EqualTo(ConfigurationStatus.Conflict));
        Assert.That(editor.FileStatus, Does.Contain("Conflict"));
    }

    [Test]
    public async Task RemovalNeverCascadesAndUnknownPresetCannotBeAuthored()
    {
        (_, ConfigurationEditorSession editor, _) = await Create();
        editor.AddCommand(editor.Token, new("new", "New", []));
        editor.PutBinding(editor.Token, "root", new("ref", "root", new("R"), new BindingDestination.Command("new")), true);
        Assert.That(editor.RemoveNewCommand(editor.Token, "new"), Is.False);
        editor.RemoveBinding(editor.Token, "root", "ref");
        Assert.That(editor.Document.Commands.Any(item => item.Id == "new"), Is.True);
        Assert.That(editor.RemoveNewCommand(editor.Token, "new"), Is.True);
        Assert.That(editor.SetPresetInput(editor.Token, "child", "run", "unknown", true, "private"), Is.False);
    }

    [Test]
    public async Task ManagementSavePreservesDraftsWithoutExecutionOrOperations()
    {
        ConfigurationDocument source = ConfigurationDocument.FromSnapshot(Disorderly.Testing.Commands.Editing.EditingFixture.Store().Current!);
        ConfigurationFixture.Files files = new()
        {
            Content = ConfigurationCodec.Encode(source)
        };
        ConfigurationService service = ConfigurationFixture.Service(files);
        await service.ReloadAsync();
        PaletteInteractionTests.View view = new();
        Disorderly.Testing.Execution.ExecutionFixture.Launcher launcher = new();
        using Disorderly.Services.CommandPaletteController palette = new(view, service.Store, Disorderly.Testing.Execution.ExecutionFixture.Coordinator(launcher));
        Disorderly.Operations.OperationCoordinator operations = new(new Disorderly.Testing.Operations.OperationFixture.Runner());
        palette.Open();
        view.Key("D");
        view.Key("N");
        Disorderly.Services.PalettePresentation selected = view.Presentation;
        view.Edit(new(selected.ActivationId, selected.Selection!.Id, selected.Editing!.SessionId, selected.Editing.Revision, "count", new Disorderly.Commands.Editing.ArgumentEdit.RawText("-")));
        palette.Dismiss();
        Guid draftId = palette.DraftStore.Drafts.Single().Id;
        ConfigurationEditorSession editor = new(service);
        CommandDefinition command = editor.Document.Commands.Single();
        editor.UpdateCommand(editor.Token, command.Id, command with { DisplayName = "Renamed" });
        Assert.That((await editor.SaveAsync())!.Success, Is.True);
        palette.Open();
        Assert.That(view.Presentation.ResumeSuggestion, Is.Not.Null);
        Assert.That(palette.DraftStore.Drafts.Single().Id, Is.EqualTo(draftId));
        palette.Dismiss();
        command = editor.Document.Commands.Single();
        editor.UpdateCommand(editor.Token, command.Id, command with { Parameters = command.Parameters.Select(item => item.Id == "count" ? item with { ValueType = ParameterValueType.Text } : item).ToArray() });
        Assert.That((await editor.SaveAsync())!.Success, Is.True);
        palette.Open();
        Assert.That(view.Presentation.ResumeSuggestion, Is.Null);
        Assert.That(palette.DraftStore.Drafts.Single().Id, Is.EqualTo(draftId));
        Assert.That(launcher.Calls, Is.Empty);
        Assert.That(operations.Operations, Is.Empty);
    }

    [Test]
    public async Task UnknownRemovalDoesNotMutateAndDiscardInvalidatesOldSessionEvents()
    {
        (_, ConfigurationEditorSession editor, _) = await Create();
        ConfigurationEditToken old = editor.Token;
        Assert.That(editor.RemoveNewCommand(old, "unknown"), Is.False);
        Assert.That(editor.RemoveNewContext(old, "unknown"), Is.False);
        Assert.That(editor.RemoveBinding(old, "root", "unknown"), Is.False);
        Assert.That(editor.IsDirty, Is.False);
        editor.AddCommand(old, new("new", "New", []));
        editor.AdoptPublished(true);
        Assert.That(editor.AddCommand(old, new("stale", "Stale", [])), Is.False);
        Assert.That(editor.IsDirty, Is.False);
    }
}