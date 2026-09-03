using System.Diagnostics;

namespace QbitFlow.Engine.Actions;

/// <summary>
/// Runs an external command with a timeout and captured output. Ported from the legacy
/// <c>Utils.Cmd</c>.
/// </summary>
public static class ProcessRunner
{
    public readonly record struct Result(int ExitCode, string StdOut, string StdErr, bool TimedOut);

    public static async Task<Result> RunAsync(
        string command,
        IReadOnlyList<string> args,
        string? workingDir,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var psi = new ProcessStartInfo
        {
            FileName = command,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (workingDir is not null) psi.WorkingDirectory = workingDir;
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stdErrTask = process.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            return new Result(-1, SafeResult(stdOutTask), SafeResult(stdErrTask), TimedOut: true);
        }

        return new Result(process.ExitCode, await stdOutTask, await stdErrTask, TimedOut: false);
    }

    /// <summary>Runs <c>shebang /c script</c> (Windows) or <c>shebang -lc script</c> (POSIX).</summary>
    public static Task<Result> RunShebangAsync(
        string shebang, string script, string? workingDir, TimeSpan timeout, CancellationToken ct = default)
    {
        var flag = OperatingSystem.IsWindows() ? "/c" : "-lc";
        return RunAsync(shebang, [flag, script], workingDir, timeout, ct);
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* best effort */ }
    }

    private static string SafeResult(Task<string> t)
    {
        try { return t.IsCompletedSuccessfully ? t.Result : ""; } catch { return ""; }
    }
}
