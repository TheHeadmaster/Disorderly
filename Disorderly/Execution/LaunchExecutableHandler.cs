using System.ComponentModel;
using System.Diagnostics;
using Disorderly.Commands;
using Serilog;

namespace Disorderly.Execution;

public sealed record ProcessLaunchResult(ExecutionOutcome Outcome, string Code, string Message);

public interface IProcessLauncher
{
    Task<ProcessLaunchResult> LaunchAsync(ProcessStartInfo startInfo);
}

public sealed class LaunchExecutableHandler(IProcessLauncher launcher) : ICommandExecutionHandler
{
    private readonly IProcessLauncher launcher = launcher;

    public bool Supports(CommandExecutionSpecification specification) => specification is CommandExecutionSpecification.LaunchExecutable;

    public async Task<ExecutionResult> ExecuteAsync(ExecutionReadyRequest request)
    {
        CommandInvocation invocation = request.Invocation;
        if (!invocation.IsExecutionReady || invocation.Command.Execution is not CommandExecutionSpecification.LaunchExecutable specification)
        {
            return ExecutionResult.For(request, ExecutionOutcome.Rejected, "InvalidLaunchContract", "The invocation is not a validated executable launch.");
        }

        if (invocation.Arguments.Assignments.FirstOrDefault(argument => argument.ParameterId == specification.ExecutableParameterId)?.Value is not ArgumentValue.Path target
            || !IsExecutablePath(target.Value))
        {
            return ExecutionResult.For(request, ExecutionOutcome.Rejected, "InvalidExecutablePath", "Choose a fully qualified executable (.exe) path without arguments, URLs or scripts.");
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = target.Value,
            UseShellExecute = false,
            Verb = string.Empty,
        };
        ProcessLaunchResult result = await this.launcher.LaunchAsync(startInfo).ConfigureAwait(false);
        return ExecutionResult.For(request, result.Outcome, result.Code, result.Message);
    }

    /// <summary>Windows syntax checks only. No existence check or identity/TOCTOU guarantee.</summary>
    public static bool IsExecutablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(character => char.IsControl(character) || character is '"' or '<' or '>' or '|' or '?' or '*')
            || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string normalized = path.Replace('/', '\\');
        // Deliberately exclude device/extended namespaces and drive-relative or root-relative names.
        bool drive = normalized.Length > 3 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '\\';
        bool unc = normalized.StartsWith("\\\\", StringComparison.Ordinal) && !normalized.StartsWith("\\\\?\\", StringComparison.Ordinal)
            && !normalized.StartsWith("\\\\.\\", StringComparison.Ordinal);
        if (!drive && !unc)
        {
            return false;
        }

        string tail = drive ? normalized[3..] : normalized[2..];
        string[] segments = tail.Split('\\');
        return segments.Length >= (unc ? 3 : 1) && segments.All(segment => segment.Length > 0
            && segment is not ("." or "..") && !segment.EndsWith('.') && !segment.EndsWith(' ') && !segment.Contains(':'));
    }
}

/// <summary>Direct launch only. Disposing the local handle does not wait for or terminate the child.</summary>
public sealed class WindowsProcessLauncher : IProcessLauncher
{
    public Task<ProcessLaunchResult> LaunchAsync(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (!LaunchExecutableHandler.IsExecutablePath(startInfo.FileName) || startInfo.UseShellExecute
            || startInfo.ArgumentList.Count != 0 || !string.IsNullOrEmpty(startInfo.Arguments)
            || !string.IsNullOrEmpty(startInfo.Verb) || !string.IsNullOrEmpty(startInfo.UserName))
        {
            return Task.FromResult(new ProcessLaunchResult(ExecutionOutcome.Rejected, "UnsafeLaunchOptions", "Direct executable launching requires an explicit executable with no shell, arguments or credentials."));
        }

        // Capture only immutable approved intent before dispatching. Do not retain a mutable options object.
        string executable = startInfo.FileName;
        return Task.Run(() => Start(executable));
    }

    private static ProcessLaunchResult Start(string executable)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ProcessLaunchResult(ExecutionOutcome.Rejected, "WindowsRequired", "Executable launching requires Windows.");
        }

        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo { FileName = executable, UseShellExecute = false });
        }
        catch (Win32Exception error)
        {
            // Only known CreateProcess rejection codes establish non-start. Unknown errors stay uncertain.
            bool notStarted = error.NativeErrorCode is 2 or 3 or 5 or 11 or 15 or 32 or 193 or 216 or 267 or 740;
            return new ProcessLaunchResult(notStarted ? ExecutionOutcome.FailedNotStarted : ExecutionOutcome.Uncertain,
                $"Win32.{error.NativeErrorCode}", notStarted
                    ? $"Windows did not start the executable (error {error.NativeErrorCode}). Check its location, format and permissions."
                    : "Windows could not establish the launch outcome. Do not retry blindly.");
        }
        catch (Exception)
        {
            return new ProcessLaunchResult(ExecutionOutcome.Uncertain, "UnexpectedLaunchFailure", "The launch outcome could not be established. Do not retry blindly.");
        }

        if (process is null)
        {
            return new ProcessLaunchResult(ExecutionOutcome.Uncertain, "NoProcessHandle", "No process handle was returned; the launch outcome is uncertain. Do not retry blindly.");
        }

        try
        {
            process.Dispose();
        }
        catch (Exception error)
        {
            // Start already succeeded: cleanup cannot turn it into a retryable non-start failure.
            Log.Warning("Local process handle cleanup raised {ExceptionType} after a successful start.", error.GetType().Name);
        }

        return new ProcessLaunchResult(ExecutionOutcome.Succeeded, "ProcessStarted", "Windows accepted the process launch.");
    }
}