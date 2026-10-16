using Disorderly.Commands;
using NUnit.Framework;
using static Disorderly.Testing.Commands.CommandTestData;

namespace Disorderly.Testing.Commands;

[TestFixture]
public class CommandCatalogTests
{
    [Test]
    public void IdentityLookupAndDeterministicEnumerationDoNotDependOnDisplayName()
    {
        CommandCatalogSnapshot catalog = Catalog(Definition("z"), Definition("a") with { DisplayName = "Open Folder" });
        Assert.That(catalog.Definitions.Select(static command => command.Id), Is.EqualTo(new[] { "a", "z" }));
        Assert.That(catalog.Find("a")!.DisplayName, Is.EqualTo(catalog.Find("z")!.DisplayName));
        CommandCatalogSnapshot renamed = Catalog(Definition("a") with { DisplayName = "Renamed" });
        Assert.That(renamed.Find("a")!.Id, Is.EqualTo("a"));
        Assert.That(catalog.Find("absent"), Is.Null);
    }

    [Test]
    public void DuplicateCommandsAndMalformedDefinitionsAreStructuredFailures()
    {
        AssertCode([Definition(), Definition()], CommandDiagnosticCode.DuplicateCommand);
        AssertCode([Definition("bad id")], CommandDiagnosticCode.InvalidIdentifier);
        AssertCode([Definition() with { DisplayName = " " }], CommandDiagnosticCode.InvalidDefinition);
        AssertCode([Definition() with { Parameters = null! }], CommandDiagnosticCode.InvalidDefinition);
        AssertCode([null!], CommandDiagnosticCode.InvalidDefinition);
        AssertCode(null!, CommandDiagnosticCode.InvalidDefinition);
    }

    [Test]
    public void ParameterIdsAreCommandLocalAndMetadataIsValidated()
    {
        Assert.That(new CommandCatalogValidator().Validate([Definition("one"), Definition("two")]).IsValid, Is.True);
        ParameterDefinition parameter = new("path", "Path", ParameterValueType.Path);
        AssertCode([Definition() with { Parameters = [parameter, parameter] }], CommandDiagnosticCode.DuplicateParameter);
        AssertCode([Definition() with { Parameters = [parameter with { Id = "bad id" }] }], CommandDiagnosticCode.InvalidParameter);
        AssertCode([Definition() with { Parameters = [parameter with { Name = " " }] }], CommandDiagnosticCode.InvalidParameter);
        AssertCode([Definition() with { Parameters = [parameter with { ValueType = (ParameterValueType)99 }] }], CommandDiagnosticCode.InvalidParameter);
        AssertCode([Definition() with { Parameters = [null!] }], CommandDiagnosticCode.InvalidParameter);
        AssertCode([Definition(policy: (CompletionPolicy)99)], CommandDiagnosticCode.InvalidCompletionPolicy);
    }

    [Test]
    public void IconsAreStructuralMetadataAndNeverReadFiles()
    {
        Assert.That(Catalog(Definition()).Find("open")!.EffectiveIcon, Is.TypeOf<CommandIcon.Automatic>());
        Assert.That(new CommandCatalogValidator().Validate([Definition(icon: new CommandIcon.CustomFile(@"Z:\nonexistent\icon.ico"))]).IsValid, Is.True);
        Assert.That(new CommandCatalogValidator().Validate([Definition(icon: new CommandIcon.BuiltIn("generic-folder"))]).IsValid, Is.True);
        AssertCode([Definition(icon: new CommandIcon.CustomFile(" "))], CommandDiagnosticCode.InvalidIcon);
        AssertCode([Definition(icon: new CommandIcon.CustomFile("bad\0icon"))], CommandDiagnosticCode.InvalidIcon);
        AssertCode([Definition(icon: new CommandIcon.BuiltIn("bad id"))], CommandDiagnosticCode.InvalidIcon);
        CommandCatalogSnapshot catalog = Catalog(Definition(required: false, icon: new CommandIcon.CustomFile(@"Z:\missing\icon.ico")));
        Assert.That(CommandInvocationFactory.PrepareDirect(catalog, "open").Invocation!.PrepareAttempt().Succeeded, Is.True);
    }

    [Test]
    public void PublishedDefinitionsCopySourceCollectionsAndCannotBeMutated()
    {
        List<ParameterDefinition> parameters = [new("path", "Path", ParameterValueType.Path)];
        List<CommandDefinition> source = [new("open", "Open", parameters)];
        CommandCatalogSnapshot catalog = new CommandCatalogValidator().Validate(source).Snapshot!;
        parameters.Clear();
        source.Clear();
        Assert.That(catalog.Find("open")!.Parameters, Has.Count.EqualTo(1));
        Assert.Throws<NotSupportedException>(() => ((IList<ParameterDefinition>)catalog.Find("open")!.Parameters).Clear());
    }

    private static void AssertCode(IReadOnlyList<CommandDefinition> definitions, CommandDiagnosticCode code)
    {
        CommandCatalogValidationResult result = new CommandCatalogValidator().Validate(definitions);
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Diagnostics.Any(diagnostic => diagnostic.Code == code && diagnostic.Severity == CommandDiagnosticSeverity.Error), Is.True, code.ToString());
    }
}