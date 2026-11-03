namespace Tanjelo.Setup;

/// <summary>
/// Setup's log, %TEMP%\Tanjelo-Setup.log by default: what it did, and why it failed, for silent runs that have no
/// window to say so. The elevated helper writes to the same file.
/// </summary>
sealed class Log(string path)
{
    public string FilePath => path;

    public static Log Default() => new(Path.Combine(Path.GetTempPath(), "Tanjelo-Setup.log"));

    public void Write(string message)
    {
        try
        {
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>A reason setup stopped, in words fit to show, with the exit code a silent run ends with.</summary>
sealed class SetupException(string message, int exitCode = ExitCodes.Failed) : Exception(message)
{
    public int ExitCode => exitCode;
}

/// <summary>What Setup.exe and the uninstaller end with, for winget and scripts.</summary>
static class ExitCodes
{
    public const int Done = 0;
    public const int Failed = 1;
    public const int Cancelled = 2;
    public const int BadCommandLine = 3;
    public const int NoAdministrator = 4;
}
