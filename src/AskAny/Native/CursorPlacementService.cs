using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace AskAny.Native;

public static class CursorPlacementService
{
    private const uint MonitorDefaultToNearest = 2;
    private const int Offset = 12;

    public static Point GetPopupLocation(Window window)
    {
        if (!GetCursorPos(out var cursorPoint))
        {
            return GetFallbackLocation(window);
        }

        var monitor = MonitorFromPoint(cursorPoint, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        var workArea = GetMonitorInfo(monitor, ref monitorInfo)
            ? monitorInfo.WorkArea
            : new Rect(0, 0, (int)SystemParameters.PrimaryScreenWidth, (int)SystemParameters.PrimaryScreenHeight);

        var dpi = VisualTreeHelper.GetDpi(window);
        var left = workArea.Left / dpi.DpiScaleX;
        var top = workArea.Top / dpi.DpiScaleY;
        var right = workArea.Right / dpi.DpiScaleX;
        var bottom = workArea.Bottom / dpi.DpiScaleY;
        var cursorX = cursorPoint.X / dpi.DpiScaleX;
        var cursorY = cursorPoint.Y / dpi.DpiScaleY;
        var width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        var height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;

        var x = cursorX + Offset;
        var y = cursorY + Offset;
        if (x + width > right)
        {
            x = cursorX - width - Offset;
        }

        if (y + height > bottom)
        {
            y = cursorY - height - Offset;
        }

        x = Math.Max(left + 8, Math.Min(x, right - width - 8));
        y = Math.Max(top + 8, Math.Min(y, bottom - height - 8));
        return new Point(x, y);
    }

    private static Point GetFallbackLocation(Window window)
    {
        var workArea = SystemParameters.WorkArea;
        return new Point(
            workArea.Left + Math.Max(12, (workArea.Width - window.Width) / 2),
            workArea.Top + Math.Max(12, (workArea.Height - window.Height) / 3));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public static implicit operator Rect(NativeRect rectangle)
        {
            return new Rect(
                rectangle.Left,
                rectangle.Top,
                rectangle.Right - rectangle.Left,
                rectangle.Bottom - rectangle.Top);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(
        NativePoint point,
        uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(
        IntPtr monitor,
        ref MonitorInfo monitorInfo);
}
