using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Nexus.Database;

namespace Nexus.App.Services;

/// <summary>
/// Gives memory back after a burst of work (scan finished, window in the background). Event-driven: there is no periodic
/// timer, so an idle application stays idle.
/// </summary>
public sealed class MemoryTrimmer
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    private readonly DispatcherTimer _delay = new() { Interval = TimeSpan.FromSeconds(3) };

    public MemoryTrimmer()
    {
        _delay.Tick += (_, _) =>
        {
            _delay.Stop();
            _ = Task.Run(Trim);
        };
    }

    /// <summary>Asks for a trim a few seconds from now (repeated calls are merged).</summary>
    public void Request()
    {
        _delay.Stop();
        _delay.Start();
    }

    private static void Trim()
    {
        NexusDatabase.ReleaseAll(); // pooled connections each keep a page cache
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
    }
}
