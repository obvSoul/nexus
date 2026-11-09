using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Nexus.Infrastructure;

/// <summary>Measures how busy the machine is *because of other processes* (system load minus our own).</summary>
public sealed class CpuMonitor
{
    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    private readonly Process _self = Process.GetCurrentProcess();
    private long _prevIdle, _prevTotal;
    private TimeSpan _prevProc;
    private long _prevWall;
    private bool _primed;

    /// <summary>0..1. Call at most every few hundred ms; the first call returns 0.</summary>
    public double SampleOtherLoad()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        _self.Refresh();
        TimeSpan proc = _self.TotalProcessorTime;
        long wall = Stopwatch.GetTimestamp();
        long total = kernel + user; // kernel time includes idle time
        double result = 0;
        if (_primed)
        {
            long dTotal = total - _prevTotal, dIdle = idle - _prevIdle;
            if (dTotal > 0)
            {
                double system = 1.0 - (double)dIdle / dTotal;
                double wallSec = (wall - _prevWall) / (double)Stopwatch.Frequency;
                double self = wallSec > 0 ? (proc - _prevProc).TotalSeconds / (wallSec * Environment.ProcessorCount) : 0;
                result = Math.Clamp(system - self, 0, 1);
            }
        }
        _prevIdle = idle; _prevTotal = total; _prevProc = proc; _prevWall = wall; _primed = true;
        return result;
    }
}

public static class BackgroundMode
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern bool SetThreadPriority(IntPtr thread, int priority);

    private const int THREAD_MODE_BACKGROUND_BEGIN = 0x00010000;

    /// <summary>Lowers CPU, disk-I/O and memory priority of the calling thread (must be called on that thread).</summary>
    public static bool BeginOnCurrentThread() => SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN);
}
