using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FishPet;

// Whether the window in front fills its whole screen (a game, a video, a slide show): not one of her own, and
// not the desktop. The same test as the BigBlueFish pet's, without Windows Forms.
internal static class Fullscreen
{
    internal static bool InFront()
    {
        var window = GetForegroundWindow();
        if (window == 0) return false;
        GetWindowThreadProcessId(window, out var id);
        if (id == Environment.ProcessId || !GetWindowRect(window, out var rect)) return false;
        try { if (Process.GetProcessById((int)id).ProcessName == "explorer") return false; } catch { }
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(window, 2), ref info)) return false;
        var screen = info.Monitor;
        return rect.Left <= screen.Left && rect.Top <= screen.Top && rect.Right >= screen.Right && rect.Bottom >= screen.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
