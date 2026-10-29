using System.Diagnostics;
using System.Text;

namespace Tanjelo.Core;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    /// <summary>The last non-empty stderr line, which is where CLI tools usually put the actual error.</summary>
    public string LastError =>
        StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? $"exit code {ExitCode}";
}

public static class ProcessRunner
{
    const int MaxCapturedChars = 64 * 1024;

    public static async Task<ProcessResult> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct, Action<string>? onStdOut = null,
        string? workingDirectory = null, Action<string>? onStdErr = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? "",
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            Append(stdout, e.Data);
            onStdOut?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            Append(stderr, e.Data);
            onStdErr?.Invoke(e.Data);
        };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ConversionException(string.Format(Strings.CouldnTStart, Path.GetFileName(exe), ex.Message), ex);
        }
        // A closed stdin makes any unexpected prompt (e.g. "enter password") fail instead of hanging forever.
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        process.WaitForExit(); // flushes the async output readers
        lock (stdout) lock (stderr)
            return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    static void Append(StringBuilder sb, string line)
    {
        lock (sb)
        {
            if (sb.Length > MaxCapturedChars)
                sb.Remove(0, sb.Length - MaxCapturedChars / 2);
            sb.AppendLine(line);
        }
    }
}
