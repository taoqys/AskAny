using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace AskAny.Native;

public static class CursorPlacementService
{
    private const int Offset = 12;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    public static void PlaceWindow(Window window)
    {
        var windowHandle = new WindowInteropHelper(window).Handle;
        if (windowHandle == IntPtr.Zero ||
            !GetCursorPos(out var cursor) ||
            !GetWindowRect(windowHandle, out var windowRect))
        {
            return;
        }

        var screen = Forms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y));
        var workArea = screen.WorkingArea;
        var width = windowRect.Right - windowRect.Left;
        var height = windowRect.Bottom - windowRect.Top;
        var minX = workArea.Left + 8;
        var minY = workArea.Top + 8;
        var maxX = Math.Max(minX, workArea.Right - width - 8);
        var maxY = Math.Max(minY, workArea.Bottom - height - 8);

        var candidates = new[]
        {
            new Point(cursor.X + Offset, cursor.Y + Offset),
            new Point(cursor.X - width - Offset, cursor.Y + Offset),
            new Point(cursor.X + Offset, cursor.Y - height - Offset),
            new Point(cursor.X - width - Offset, cursor.Y - height - Offset)
        };

        var location = candidates
            .Select(candidate => new Point(
                Math.Clamp(candidate.X, minX, maxX),
                Math.Clamp(candidate.Y, minY, maxY)))
            .OrderBy(candidate => DistanceToRectangle(
                candidate.X,
                candidate.Y,
                width,
                height,
                cursor.X,
                cursor.Y))
            .First();

        SetWindowPos(
            windowHandle,
            IntPtr.Zero,
            (int)Math.Round(location.X),
            (int)Math.Round(location.Y),
            0,
            0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    private static double DistanceToRectangle(
        double x,
        double y,
        double width,
        double height,
        double cursorX,
        double cursorY)
    {
        var dx = Math.Max(Math.Max(x - cursorX, cursorX - (x + width)), 0);
        var dy = Math.Max(Math.Max(y - cursorY, cursorY - (y + height)), 0);
        return (dx * dx) + (dy * dy);
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
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
