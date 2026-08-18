using System.Runtime.InteropServices;

namespace MorerialsAvalonia.Native;

internal static unsafe partial class WindowsNative
{
    internal const uint MonitorDefaultToNearest = 2;
    internal const uint WdaNone = 0;
    internal const uint WdaExcludeFromCapture = 0x11;
    private const uint SpiGetClientAreaAnimation = 0x1042;
    private const uint GwHwndPrev = 3;
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExLayered = 0x00080000L;
    private const uint DwmwaExtendedFrameBounds = 9;
    private const uint DwmwaCloaked = 14;

    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int SetWindowDisplayAffinity(nint hwnd, uint affinity);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int GetWindowDisplayAffinity(nint hwnd, out uint affinity);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int ClientToScreen(nint hwnd, ref NativePoint point);

    [LibraryImport("user32.dll")]
    private static partial int IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int IsIconic(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint hwnd, uint command);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int GetClientRect(nint hwnd, out NativeRect rect);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int GetWindowRect(nint hwnd, out NativeRect rect);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static partial int DwmGetWindowAttributeInt(
        nint hwnd,
        uint attribute,
        out int value,
        uint valueSize);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static partial int DwmGetWindowAttributeRect(
        nint hwnd,
        uint attribute,
        out NativeRect value,
        uint valueSize);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static partial int SystemParametersInfo(uint action, uint parameter, out int value, uint flags);

    [LibraryImport("kernel32.dll")]
    internal static partial int CloseHandle(nint handle);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);

    internal static void SetProcessAppUserModelId(string appId)
        => Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(appId));

    internal static bool TryGetWindowDisplayAffinity(nint hwnd, out uint affinity)
        => GetWindowDisplayAffinity(hwnd, out affinity) != 0;

    internal static NativeRect GetMonitorRect(nint monitor)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfoW(monitor, ref info) == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return info.Monitor;
    }

    internal static NativePoint GetClientScreenOrigin(nint hwnd)
    {
        var point = new NativePoint();
        if (ClientToScreen(hwnd, ref point) == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return point;
    }

    internal static bool AreClientAnimationsEnabled()
        => SystemParametersInfo(SpiGetClientAreaAnimation, 0, out var enabled, 0) == 0 || enabled != 0;

    internal static bool IsWindowFullyOccluded(nint hwnd)
    {
        if (hwnd == 0 || IsWindowVisible(hwnd) == 0 || IsIconic(hwnd) != 0 || IsWindowCloaked(hwnd))
            return true;

        if (GetClientRect(hwnd, out var clientRect) == 0)
            return false;

        var topLeft = new NativePoint { X = clientRect.Left, Y = clientRect.Top };
        var bottomRight = new NativePoint { X = clientRect.Right, Y = clientRect.Bottom };
        if (ClientToScreen(hwnd, ref topLeft) == 0 || ClientToScreen(hwnd, ref bottomRight) == 0)
            return false;

        var target = new NativeRect
        {
            Left = topLeft.X,
            Top = topLeft.Y,
            Right = bottomRight.X,
            Bottom = bottomRight.Y
        };
        if (target.Width <= 0 || target.Height <= 0)
            return true;

        var processId = (uint)Environment.ProcessId;
        var candidate = GetWindow(hwnd, GwHwndPrev);
        for (var inspected = 0; candidate != 0 && inspected < 256; inspected++)
        {
            if (IsWindowVisible(candidate) != 0 && IsIconic(candidate) == 0 && !IsWindowCloaked(candidate))
            {
                GetWindowThreadProcessId(candidate, out var candidateProcessId);
                var exStyle = GetWindowLongPtr(candidate, GwlExStyle).ToInt64();
                var canReliablyOcclude = candidateProcessId != processId &&
                    (exStyle & (WsExTransparent | WsExLayered)) == 0;
                if (canReliablyOcclude && TryGetWindowBounds(candidate, out var bounds) &&
                    bounds.Left <= target.Left && bounds.Top <= target.Top &&
                    bounds.Right >= target.Right && bounds.Bottom >= target.Bottom)
                    return true;
            }

            candidate = GetWindow(candidate, GwHwndPrev);
        }

        return false;
    }

    private static bool IsWindowCloaked(nint hwnd)
        => DwmGetWindowAttributeInt(
               hwnd,
               DwmwaCloaked,
               out var cloaked,
               sizeof(int)) == 0 && cloaked != 0;

    private static bool TryGetWindowBounds(nint hwnd, out NativeRect bounds)
    {
        if (DwmGetWindowAttributeRect(
                hwnd,
                DwmwaExtendedFrameBounds,
                out bounds,
                (uint)Marshal.SizeOf<NativeRect>()) == 0)
            return bounds.Width > 0 && bounds.Height > 0;

        return GetWindowRect(hwnd, out bounds) != 0 && bounds.Width > 0 && bounds.Height > 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }
}
