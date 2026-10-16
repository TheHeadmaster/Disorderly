using Disorderly.Commands;
using NUnit.Framework;
using static Disorderly.Testing.Commands.CommandTestData;

namespace Disorderly.Testing.Commands;

[TestFixture]
public class ArgumentContractTests
{
    [Test]
    public void MissingRequiredIsIncompleteWhileOptionalAbsenceIsValid()
    {
        ArgumentValidationResult missing = ArgumentValidator.Validate(Catalog(Definition()).Find("open")!, ArgumentSet.Empty);
        Assert.That(missing.Status, Is.EqualTo(ArgumentValidationStatus.Incomplete));
        Assert.That(missing.Diagnostics.Single().Code, Is.EqualTo(CommandDiagnosticCode.MissingRequired));
        Assert.That(missing.Diagnostics.Single().ParameterId, Is.EqualTo("path"));
        Assert.That(ArgumentValidator.Validate(Catalog(Definition(required: false)).Find("open")!, ArgumentSet.Empty).Status, Is.EqualTo(ArgumentValidationStatus.Valid));
    }

    [Test]
    public void FalseZeroAndEmptyTextArePresentAndTypedValuesHaveValueEquality()
    {
        CommandDefinition definition = new("values", "Values", [new("flag", "Flag", ParameterValueType.Boolean, true),
            new("number", "Number", ParameterValueType.Integer, true), new("text", "Text", ParameterValueType.Text, true)]);
        ArgumentSet arguments = new([new("flag", new ArgumentValue.Boolean(false)), new("number", new ArgumentValue.Integer(0)), new("text", new ArgumentValue.Text(""))]);
        Assert.That(ArgumentValidator.Validate(Catalog(definition).Find("values")!, arguments).Status, Is.EqualTo(ArgumentValidationStatus.Valid));
        Assert.That(new ArgumentValue.Boolean(false), Is.EqualTo(new ArgumentValue.Boolean(false)));
        Assert.That(new ArgumentValue.Integer(0), Is.EqualTo(new ArgumentValue.Integer(0)));
        Assert.That(new ArgumentValue.Text("same"), Is.EqualTo(new ArgumentValue.Text("same")));
        Assert.That(new ArgumentValue.Path("same"), Is.EqualTo(new ArgumentValue.Path("same")));
        Assert.That(new ArgumentValue.Path("same"), Is.Not.EqualTo(new ArgumentValue.Text("same")));
    }

    [TestCase(ParameterValueType.Text)]
    [TestCase(ParameterValueType.Boolean)]
    [TestCase(ParameterValueType.Integer)]
    [TestCase(ParameterValueType.Path)]
    public void IncompatibleTypesAreNeverCoerced(ParameterValueType type)
    {
        CommandDefinition command = Catalog(new CommandDefinition("typed", "Typed", [new("value", "Value", type, true)])).Find("typed")!;
        ArgumentValue wrong = type == ParameterValueType.Boolean ? new ArgumentValue.Text("false") : new ArgumentValue.Boolean(false);
        ArgumentValidationResult result = ArgumentValidator.Validate(command, new ArgumentSet([new("value", wrong)]));
        Assert.That(result.Status, Is.EqualTo(ArgumentValidationStatus.Invalid));
        Assert.That(result.Diagnostics.Single().Code, Is.EqualTo(CommandDiagnosticCode.WrongType));
    }

    [Test]
    public void UnknownDuplicateMalformedAndNullAssignmentsArePreservedAndRejected()
    {
        CommandDefinition command = Catalog(Definition(required: false)).Find("open")!;
        AssertCode(command, new ArgumentSet([new("unknown", new ArgumentValue.Text("value"))]), CommandDiagnosticCode.UnknownParameter);
        ArgumentSet duplicate = new([new("path", new ArgumentValue.Path("one")), new("path", new ArgumentValue.Path("two"))]);
        Assert.That(duplicate.Assignments, Has.Length.EqualTo(2));
        AssertCode(command, duplicate, CommandDiagnosticCode.DuplicateArgument);
        AssertCode(command, new ArgumentSet([new("bad id", new ArgumentValue.Text("value"))]), CommandDiagnosticCode.InvalidArguments);
        AssertCode(command, new ArgumentSet([null!]), CommandDiagnosticCode.InvalidArguments);
        AssertCode(command, new ArgumentSet(null!), CommandDiagnosticCode.InvalidArguments);
        AssertCode(command, new ArgumentSet([new("path", null!)]), CommandDiagnosticCode.InvalidValue);
        AssertCode(command, new ArgumentSet([new("path", new ArgumentValue.Path(" "))]), CommandDiagnosticCode.InvalidValue);
        AssertCode(command, new ArgumentSet([new("label", new ArgumentValue.Text(null!))]), CommandDiagnosticCode.InvalidValue);
    }

    [Test]
    public void PathsRemainOpaqueAndArgumentsAreCopiedInDeterministicOrder()
    {
        List<ArgumentAssignment> source = [new("recursive", new ArgumentValue.Boolean(false)), new("path", new ArgumentValue.Path(@"Z:\not-present\relative..\file"))];
        ArgumentSet arguments = new(source);
        source.Clear();
        Assert.That(arguments.Assignments.Select(static assignment => assignment.ParameterId), Is.EqualTo(new[] { "path", "recursive" }));
        Assert.That(ArgumentValidator.Validate(Catalog(Definition()).Find("open")!, arguments).Status, Is.EqualTo(ArgumentValidationStatus.Valid));
        Assert.That(((ArgumentValue.Path)arguments.Assignments[0].Value).Value, Is.EqualTo(@"Z:\not-present\relative..\file"));
    }

    private static void AssertCode(CommandDefinition definition, ArgumentSet arguments, CommandDiagnosticCode code)
    {
        ArgumentValidationResult result = ArgumentValidator.Validate(definition, arguments);
        Assert.That(result.Status, Is.EqualTo(ArgumentValidationStatus.Invalid));
        Assert.That(result.Diagnostics.Any(diagnostic => diagnostic.Code == code), Is.True, code.ToString());
    }
}