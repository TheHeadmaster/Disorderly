using System.Diagnostics;
using Disorderly.Commands;
using Disorderly.Execution;
using NUnit.Framework;

namespace Disorderly.Testing.Execution;

[TestFixture]
public class ExecutableLaunchTests
{
    [Test]
    public async Task BindingPresetWithSpacesBecomesOneExecutableWithoutShellOrArguments()
    {
        const string target = @"C:\Program Files\Example App\example.exe";
        ExecutionFixture.Launcher launcher = new();
        CommandInvocation invocation = ExecutionFixture.Invocation(target);
        await ExecutionFixture.Coordinator(launcher).SubmitAsync(invocation.PrepareAttempt().Request!);
        ProcessStartInfo options = launcher.Calls.Single();
        Assert.That(options.FileName, Is.EqualTo(target));
        Assert.That(options.UseShellExecute, Is.False);
        Assert.That(options.ArgumentList, Is.Empty);
        Assert.That(options.Arguments, Is.Empty);
        Assert.That(options.Verb, Is.Empty);
        Assert.That(options.UserName, Is.Empty);
    }

    [TestCase("example.exe")]
    [TestCase(@"C:example.exe")]
    [TestCase(@"\example.exe")]
    [TestCase("https://example.com/example.exe")]
    [TestCase(@"C:\example.ps1")]
    [TestCase(@"C:\example.cmd")]
    [TestCase(@"C:\example.txt")]
    [TestCase(@"C:\example.exe --option")]
    [TestCase(@"C:\..\example.exe")]
    [TestCase(@"C:\example.exe:stream.exe")]
    [TestCase(@"\\?\C:\example.exe")]
    [TestCase(@"C:\bad|name.exe")]
    public async Task InvalidTargetIsRejectedBeforeCallingLauncher(string target)
    {
        ExecutionFixture.Launcher launcher = new();
        ExecutionResult result = await ExecutionFixture.Coordinator(launcher).SubmitAsync(ExecutionFixture.Invocation(target).PrepareAttempt().Request!);
        Assert.That(result.Outcome, Is.EqualTo(ExecutionOutcome.Rejected));
        Assert.That(result.Code, Is.EqualTo("InvalidExecutablePath"));
        Assert.That(launcher.Calls, Is.Empty);
    }

    [TestCase(@"C:\Tools\example.EXE")]
    [TestCase(@"\\server\share\app.exe")]
    [TestCase("C:/Tools/app.exe")]
    public void ExplicitWindowsPathsAreAcceptedWithoutFilesystemAccess(string path)
    {
        Assert.That(LaunchExecutableHandler.IsExecutablePath(path), Is.True);
    }

    [TestCase(ExecutionOutcome.Succeeded, false)]
    [TestCase(ExecutionOutcome.FailedNotStarted, true)]
    [TestCase(ExecutionOutcome.Uncertain, false)]
    public async Task LaunchResultRetainsStructuredOutcomeAndRetrySafety(ExecutionOutcome outcome, bool retry)
    {
        ExecutionFixture.Launcher launcher = new() { Start = _ => Task.FromResult(new ProcessLaunchResult(outcome, "Fake", "Useful diagnostic")) };
        ExecutionResult result = await ExecutionFixture.Coordinator(launcher).SubmitAsync(ExecutionFixture.Invocation().PrepareAttempt().Request!);
        Assert.That(result.Outcome, Is.EqualTo(outcome));
        Assert.That(result.CanRetry, Is.EqualTo(retry));
        Assert.That(result.Message, Is.EqualTo("Useful diagnostic"));
        Assert.That(launcher.Calls.Count, Is.EqualTo(1));
    }
    [TestCase("shell")]
    [TestCase("arguments")]
    [TestCase("argument-list")]
    [TestCase("verb")]
    [TestCase("credentials")]
    [TestCase("relative")]
    public async Task ProductionBoundaryRejectsUnsafeOptionsWithoutStartingAnything(string invalid)
    {
        ProcessStartInfo options = new() { FileName = @"C:\Example\application.exe", UseShellExecute = false };
        switch (invalid)
        {
            case "shell": options.UseShellExecute = true; break;
            case "arguments": options.Arguments = "--unexpected"; break;
            case "argument-list": options.ArgumentList.Add("--unexpected"); break;
            case "verb": options.Verb = "runas"; break;
            case "credentials": options.UserName = "other-user"; break;
            case "relative": options.FileName = "application.exe"; break;
        }

        ProcessLaunchResult result = await new WindowsProcessLauncher().LaunchAsync(options);
        Assert.That(result.Outcome, Is.EqualTo(ExecutionOutcome.Rejected));
        Assert.That(result.Code, Is.EqualTo("UnsafeLaunchOptions"));
    }

}