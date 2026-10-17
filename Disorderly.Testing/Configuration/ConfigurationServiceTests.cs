using Disorderly.Commands;
using Disorderly.Configuration;
using Disorderly.Routing;
using Disorderly.Services;
using NUnit.Framework;

namespace Disorderly.Testing.Configuration;

[TestFixture]
public class ConfigurationServiceTests
{
    private static ConfigurationDocument Binding(ConfigurationDocument document, bool root, Func<BindingDefinition, BindingDefinition> change)
    {
        int index = root ? 0 : 1;
        ContextDefinition context = document.Routing.Contexts[index];
        ContextDefinition replacement = context with { Bindings = [change(context.Bindings[0])] };
        return document with { Routing = document.Routing with { Contexts = document.Routing.Contexts.Select((item, at) => at == index ? replacement : item).ToArray() } };
    }

    private static ConfigurationDocument Command(ConfigurationDocument document, Func<CommandDefinition, CommandDefinition> change) =>
        document with { Commands = [change(document.Commands[0]), .. document.Commands.Skip(1)] };

    public static IEnumerable<TestCaseData> InvalidDocuments()
    {
        ConfigurationDocument d = ConfigurationFixture.Full();
        yield return new(d with { Commands = [.. d.Commands, d.Commands[0]] }, "DuplicateCommand");
        yield return new(d with { Routing = d.Routing with { Contexts = [.. d.Routing.Contexts, d.Routing.Contexts[0]] } }, "DuplicateContext");
        yield return new(Binding(d, true, binding => binding with { Id = "run" }), "DuplicateBinding");
        ContextDefinition root = d.Routing.Contexts[0];
        yield return new(d with { Routing = d.Routing with { Contexts = [root with { Bindings = [.. root.Bindings, root.Bindings[0] with { Id = "duplicate-gesture" }] }, d.Routing.Contexts[1]] } }, "DuplicateGesture");
        yield return new(Binding(d, true, binding => binding with { Destination = new BindingDestination.Context("root") }), "ContextCycle");
        ContextDefinition child = d.Routing.Contexts[1];
        yield return new(d with { Routing = d.Routing with { Contexts = [root, child with { Bindings = [.. child.Bindings, new("cycle", "child", new("Q"), new BindingDestination.Context("root"))] }] } }, "ContextCycle");
        yield return new(Binding(d, true, binding => binding with { Destination = new BindingDestination.Context("missing") }), "MissingContext");
        yield return new(Binding(d, false, binding => binding with { Destination = new BindingDestination.Command("missing") }), "MissingCommand");
        yield return new(Command(d, command => command with { Id = "invalid id" }), "InvalidIdentifier");
        yield return new(Command(d, command => command with { Parameters = [command.Parameters[0] with { Name = "" }, .. command.Parameters.Skip(1)] }), "InvalidParameter");
        yield return new(Command(d, command => command with { Parameters = [.. command.Parameters, command.Parameters[0]] }), "DuplicateParameter");
        yield return new(Binding(d, false, binding => binding with { Destination = new BindingDestination.Command("launch") { Metadata = new(new ArgumentSet([new("missing", new ArgumentValue.Text(""))])) } }), "UnknownParameter");
        yield return new(Binding(d, false, binding => binding with { Destination = new BindingDestination.Command("launch") { Metadata = new(new ArgumentSet([new("executable", new ArgumentValue.Boolean(false))])) } }), "WrongType");
        yield return new(Binding(d, false, binding => binding with { Destination = new BindingDestination.Command("launch") { Metadata = new(new ArgumentSet([new("text", new ArgumentValue.Text("")), new("text", new ArgumentValue.Text(""))])) } }), "DuplicateArgument");
        yield return new(Command(d, command => command with { Execution = new CommandExecutionSpecification.LaunchExecutable("missing") }), "InvalidExecutionSpecification");
        yield return new(Command(d, command => command with { Parameters = [command.Parameters[0] with { Required = false }, .. command.Parameters.Skip(1)] }), "InvalidExecutionSpecification");
        yield return new(Binding(d, true, binding => binding with { Gesture = LogicalGesture.Escape }), "ReservedGesture");
        yield return new(Binding(d, true, binding => binding with { Gesture = PaletteConfiguration.ActivationGesture }), "ReservedGesture");
        yield return new(Command(d, command => command with { Icon = new CommandIcon.BuiltIn("") }), "InvalidIcon");
    }

    [TestCaseSource(nameof(InvalidDocuments))]
    public async Task ExistingCombinedValidatorRejectsInvalidFileWithoutPartialPublication(ConfigurationDocument document, string code)
    {
        ConfigurationFixture.Files files = new() { Content = ConfigurationCodec.Encode(document) };
        ConfigurationService service = ConfigurationFixture.Service(files);
        CommandConfigurationSnapshot baseline = service.Store.Current!;
        byte[] original = files.Content.ToArray();
        ConfigurationResult result = await service.ReloadAsync();
        Assert.That(result.Status, Is.EqualTo(ConfigurationStatus.DomainInvalid));
        Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain(code));
        Assert.That(service.Store.Current, Is.SameAs(baseline));
        Assert.That(service.Revision, Is.Zero);
        Assert.That(files.Content, Is.EqualTo(original));
        Assert.That(files.Writes, Is.Zero);
    }

    [Test]
    public async Task MissingFileKeepsEmptyRootAndInvalidReloadKeepsLastValidSnapshot()
    {
        ConfigurationFixture.Files files = new();
        ConfigurationService service = ConfigurationFixture.Service(files);
        Assert.That((await service.ReloadAsync()).Status, Is.EqualTo(ConfigurationStatus.Missing));
        Assert.That(service.Store.Current!.Catalog.Definitions, Is.Empty);
        Assert.That(service.Store.Current.Routing.Contexts.Single().Bindings, Is.Empty);
        files.Content = ConfigurationCodec.Encode(ConfigurationFixture.Full());
        Assert.That((await service.ReloadAsync()).Status, Is.EqualTo(ConfigurationStatus.Loaded));
        CommandInteraction interaction = service.Store.CreateInteraction();
        files.Content = ConfigurationFixture.Bytes("{");
        Assert.That((await service.ReloadAsync()).Status, Is.EqualTo(ConfigurationStatus.InvalidDocument));
        Assert.That(service.Store.Current, Is.SameAs(interaction.Snapshot));
        Assert.That(files.Writes, Is.Zero);
        Assert.That(service.Revision, Is.EqualTo(1));
        files.Content = ConfigurationFixture.Bytes(ConfigurationFixture.Minimal.Replace("\"version\":1", "\"version\":2"));
        Assert.That((await service.ReloadAsync()).Status, Is.EqualTo(ConfigurationStatus.UnsupportedVersion));
        Assert.That(service.Store.Current, Is.SameAs(interaction.Snapshot));
    }

    [Test]
    public async Task ReloadRetainsActiveInteractionAndNewSessionsUseTheNewCompletePair()
    {
        ConfigurationFixture.Files files = new() { Content = ConfigurationCodec.Encode(ConfigurationFixture.Full("Old")) };
        ConfigurationService service = ConfigurationFixture.Service(files);
        await service.ReloadAsync();
        CommandInteraction old = service.Store.CreateInteraction();
        files.Content = ConfigurationCodec.Encode(ConfigurationFixture.Full("New"));
        await service.ReloadAsync();
        Assert.That(old.Snapshot.Catalog.Find("launch")!.DisplayName, Is.EqualTo("Old"));
        Assert.That(service.Store.CreateInteraction().Snapshot.Catalog.Find("launch")!.DisplayName, Is.EqualTo("New"));
        Assert.That(service.Revision, Is.EqualTo(2));
    }

    [Test]
    public async Task ReadFailuresAndUnreachableWarningsRemainDistinct()
    {
        ConfigurationFixture.Files files = new() { ReadError = new UnauthorizedAccessException("private path") };
        ConfigurationService service = ConfigurationFixture.Service(files);
        CommandConfigurationSnapshot baseline = service.Store.Current!;
        ConfigurationResult failure = await service.ReloadAsync();
        Assert.That(failure.Status, Is.EqualTo(ConfigurationStatus.IoFailure));
        Assert.That(failure.Diagnostics[0].Message, Does.Not.Contain("private path"));
        Assert.That(service.Store.Current, Is.SameAs(baseline));
        files.ReadError = null;
        ConfigurationDocument document = ConfigurationFixture.Empty;
        document = document with { Routing = document.Routing with { Contexts = [.. document.Routing.Contexts, new("unreachable", "Not connected", [])] } };
        files.Content = ConfigurationCodec.Encode(document);
        ConfigurationResult loaded = await service.ReloadAsync();
        Assert.That(loaded.Status, Is.EqualTo(ConfigurationStatus.Loaded));
        Assert.That(loaded.Diagnostics.Single().Warning, Is.True);
        Assert.That(loaded.Diagnostics.Single().Routing, Is.Not.Null);
    }

    [Test]
    public async Task ProgrammaticInvalidEnumsFailDomainValidationBeforeSaving()
    {
        ConfigurationFixture.Files files = new();
        ConfigurationService service = ConfigurationFixture.Service(files);
        ConfigurationDocument document = Command(ConfigurationFixture.Full(), command => command with { DefaultCompletionPolicy = (CompletionPolicy)99 });
        ConfigurationResult result = await service.SaveAsync(document, 0);
        Assert.That(result.Status, Is.EqualTo(ConfigurationStatus.DomainInvalid));
        Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("InvalidCompletionPolicy"));
        Assert.That(files.Writes, Is.Zero);
    }
}