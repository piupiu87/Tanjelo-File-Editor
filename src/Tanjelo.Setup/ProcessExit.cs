using System.Diagnostics;

namespace Tanjelo.Setup;

/// <summary>
/// A program's exit as a task. Windows reports an exit on a thread of its own, at any time, even after setup has
/// finished with the program, and an exception there would end setup; so the handler only marks a task done, which
/// can't throw, and whatever waits on the task carries on elsewhere.
/// </summary>
static class ProcessExit
{
    public static Task Of(Process process)
    {
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => exited.TrySetResult(true);
        if (process.HasExited) exited.TrySetResult(true);
        return exited.Task;
    }

    /// <summary>Marks a task's failure as seen, for a task setup stops waiting for, such as reading a program it stopped.</summary>
    public static void Ignore(Task task) =>
        task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
}
