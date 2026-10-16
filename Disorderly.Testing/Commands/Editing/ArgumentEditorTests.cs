using Disorderly.Commands;
using Disorderly.Commands.Editing;
using NUnit.Framework;

namespace Disorderly.Testing.Commands.Editing;

[TestFixture]
public class ArgumentEditorTests
{
    [TestCase("日本語 😀 café")]
    [TestCase("")]
    [TestCase("  unchanged spacing  ")]
    public void TextIsPresentWithoutTrimmingOrUnicodeLoss(string text)
    {
        ArgumentEditorSession editor = EditingFixture.Session(requiredText: true);
        Assert.That(editor.Snapshot.CanSubmit, Is.False);
        Assert.That(EditingFixture.Apply(editor, "text", new ArgumentEdit.RawText(text)), Is.True);
        Assert.That(editor.Snapshot.Invocation.Arguments.Assignments.Single(value => value.ParameterId == "text").Value, Is.EqualTo(new ArgumentValue.Text(text)));
        Assert.That(editor.Snapshot.CanSubmit, Is.True);
    }

    [Test]
    public void BooleanAbsenceFalseAndTrueRemainDistinct()
    {
        ArgumentEditorSession editor = EditingFixture.Session();
        Assert.That(editor.Snapshot.Invocation.Arguments.Assignments.Any(value => value.ParameterId == "flag"), Is.False);
        foreach (bool value in new[] { false, true })
        {
            EditingFixture.Apply(editor, "flag", new ArgumentEdit.Boolean(value));
            Assert.That(editor.Snapshot.Invocation.Arguments.Assignments.Single(argument => argument.ParameterId == "flag").Value, Is.EqualTo(new ArgumentValue.Boolean(value)));
        }

        EditingFixture.Apply(editor, "flag", new ArgumentEdit.Boolean(null));
        Assert.That(editor.Snapshot.Invocation.Arguments.Assignments.Any(value => value.ParameterId == "flag"), Is.False);
        Assert.That(editor.Snapshot.CanSubmit, Is.True);
    }

    [TestCase("0", 0L)]
    [TestCase("-42", -42L)]
    [TestCase("+42", 42L)]
    [TestCase("9223372036854775807", long.MaxValue)]
    [TestCase("-9223372036854775808", long.MinValue)]
    public void IntegerParsingUsesSignedInvariantInt64(string raw, long expected)
    {
        ArgumentEditorSession editor = EditingFixture.Session();
        EditingFixture.Apply(editor, "count", new ArgumentEdit.RawText(raw));
        Assert.That(editor.Snapshot.Invocation.Arguments.Assignments.Single(value => value.ParameterId == "count").Value, Is.EqualTo(new ArgumentValue.Integer(expected)));
        Assert.That(editor.Snapshot.CanSubmit, Is.True);
    }

    [TestCase("-", CommandDiagnosticCode.InvalidIntegerText)]
    [TestCase("", CommandDiagnosticCode.InvalidIntegerText)]
    [TestCase("1.5", CommandDiagnosticCode.InvalidIntegerText)]
    [TestCase(" 2", CommandDiagnosticCode.InvalidIntegerText)]
    [TestCase("2,000", CommandDiagnosticCode.InvalidIntegerText)]
    [TestCase("abc", CommandDiagnosticCode.InvalidIntegerText)]
    [TestCase("١٢", CommandDiagnosticCode.InvalidIntegerText)]
    [TestCase("9223372036854775808", CommandDiagnosticCode.IntegerOverflow)]
    [TestCase("-9223372036854775809", CommandDiagnosticCode.IntegerOverflow)]
    public void InvalidOptionalInputRetainsRawTextAndCannotReusePreviousValidValue(string raw, CommandDiagnosticCode code)
    {
        ArgumentEditorSession editor = EditingFixture.Session();
        EditingFixture.Apply(editor, "count", new ArgumentEdit.RawText("7"));
        EditingFixture.Apply(editor, "count", new ArgumentEdit.RawText(raw));
        Assert.That(editor.Snapshot.Parameters.Single(parameter => parameter.Parameter.Id == "count").RawText, Is.EqualTo(raw));
        Assert.That(editor.Snapshot.Diagnostics.Single().Code, Is.EqualTo(code));
        Assert.That(editor.Snapshot.Invocation.Arguments.Assignments.Any(value => value.ParameterId == "count"), Is.False);
        Assert.That(editor.Snapshot.Invocation.IsExecutionReady, Is.True, "The omitted optional typed assignment is valid, but its pending input is not.");
        Assert.That(editor.Snapshot.CanSubmit, Is.False);
        EditingFixture.Apply(editor, "count", new ArgumentEdit.RawText("0"));
        Assert.That(editor.Snapshot.CanSubmit, Is.True);
    }

    [TestCase("Z:/does/not/exist/日本語.exe")]
    [TestCase("  opaque path text  ")]
    public void PathEditingRetainsOpaqueTextWithoutFilesystemResolution(string path)
    {
        ArgumentEditorSession editor = EditingFixture.Session(presetPath: false);
        EditingFixture.Apply(editor, "executable", new ArgumentEdit.RawText(path));
        Assert.That(editor.Snapshot.Invocation.Arguments.Assignments.Single().Value, Is.EqualTo(new ArgumentValue.Path(path)));
        Assert.That(editor.Snapshot.CanSubmit, Is.True, "Executable syntax/existence is a later execution check.");
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("bad\0path")]
    public void InvalidPathReportsDomainBackedErrorAndPreservesRawInput(string raw)
    {
        ArgumentEditorSession editor = EditingFixture.Session(presetPath: false);
        EditingFixture.Apply(editor, "executable", new ArgumentEdit.RawText(raw));
        Assert.That(editor.Snapshot.Parameters[^1].RawText, Is.EqualTo(raw));
        Assert.That(editor.Snapshot.Diagnostics.Single().Code, Is.EqualTo(CommandDiagnosticCode.InvalidPathText));
        Assert.That(editor.Snapshot.CanSubmit, Is.False);
    }

    [Test]
    public void RevisionsPreserveIdentityProvenancePresetsAndUnrelatedAssignments()
    {
        ArgumentEditorSession editor = EditingFixture.Session();
        ArgumentEditingSnapshot original = editor.Snapshot;
        EditingFixture.Apply(editor, "text", new ArgumentEdit.RawText("first"));
        EditingFixture.Apply(editor, "flag", new ArgumentEdit.Boolean(false));
        EditingFixture.Apply(editor, "text", new ArgumentEdit.RawText("second"));
        CommandInvocation revised = editor.Snapshot.Invocation;
        Assert.That(revised.Id, Is.EqualTo(original.Invocation.Id));
        Assert.That(revised.Command, Is.SameAs(original.Invocation.Command));
        Assert.That(revised.Catalog, Is.SameAs(original.Invocation.Catalog));
        Assert.That(revised.Origin, Is.EqualTo(original.Invocation.Origin));
        Assert.That(revised.CompletionPolicy, Is.EqualTo(original.Invocation.CompletionPolicy));
        Assert.That(revised.Arguments.Assignments.Select(assignment => assignment.ParameterId).Distinct().Count(), Is.EqualTo(3));
        Assert.That(revised.Arguments.Assignments.Single(assignment => assignment.ParameterId == "executable"), Is.EqualTo(original.Invocation.Arguments.Assignments.Single()));
        Assert.That(original.Invocation.Arguments.Assignments.Length, Is.EqualTo(1));
        Assert.That(editor.Snapshot.Parameters.Select(parameter => parameter.Parameter.Id), Is.EqualTo(new[] { "text", "flag", "count", "executable" }));
    }

    [Test]
    public void PresetsUnknownParametersWrongInputKindsAndStaleEventsAreRejected()
    {
        ArgumentEditorSession editor = EditingFixture.Session();
        Assert.That(EditingFixture.Apply(editor, "executable", new ArgumentEdit.Clear()), Is.False);
        Assert.That(EditingFixture.Apply(editor, "executable", new ArgumentEdit.RawText("replacement")), Is.False);
        Assert.That(EditingFixture.Apply(editor, "missing", new ArgumentEdit.RawText("value")), Is.False);
        Assert.That(EditingFixture.Apply(editor, "flag", new ArgumentEdit.RawText("false")), Is.False);
        Assert.That(EditingFixture.Apply(editor, "text", new ArgumentEdit.Boolean(null)), Is.False);
        Assert.That(editor.TryApply(Guid.NewGuid(), 0, "text", new ArgumentEdit.RawText("value")), Is.False);
        EditingFixture.Apply(editor, "text", new ArgumentEdit.RawText("value"));
        Assert.That(editor.TryApply(editor.Id, 0, "text", new ArgumentEdit.RawText("stale")), Is.False);
        editor.SetReadOnly(true);
        Assert.That(EditingFixture.Apply(editor, "text", new ArgumentEdit.RawText("frozen")), Is.False);
    }

    [TestCase(false, true)]
    [TestCase(true, false)]
    public void ClearingUserEnteredTextReturnsToAbsence(bool required, bool ready)
    {
        ArgumentEditorSession editor = EditingFixture.Session(requiredText: required);
        EditingFixture.Apply(editor, "text", new ArgumentEdit.RawText(""));
        EditingFixture.Apply(editor, "text", new ArgumentEdit.Clear());
        Assert.That(editor.Snapshot.Invocation.Arguments.Assignments.Any(value => value.ParameterId == "text"), Is.False);
        Assert.That(editor.Snapshot.CanSubmit, Is.EqualTo(ready));
    }
}