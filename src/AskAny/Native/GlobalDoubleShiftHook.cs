using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AskAny.Native;

public sealed class GlobalDoubleShiftHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyUp = 0x0101;
    private const int VkShift = 0x10;
    private const long MinimumIntervalMs = 70;
    private const long MaximumIntervalMs = 550;

    private readonly LowLevelKeyboardProc _hookProc;
    private IntPtr _hook = IntPtr.Zero;
    private long _lastShiftUpTick;

    public event EventHandler? DoubleShiftPressed;

    public GlobalDoubleShiftHook()
    {
        _hookProc = HookCallback;
    }

    public void Install()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        using var currentProcess = Process.GetCurrentProcess();
        using var currentModule = currentProcess.MainModule!;
        _hook = SetWindowsHookEx(
            WhKeyboardLl,
            _hookProc,
            GetModuleHandle(currentModule.ModuleName),
            0);

        if (_hook == IntPtr.Zero)
        {
            throw new InvalidOperationException("无法注册全局键盘监听。");
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == WmKeyUp)
        {
            var keyboard = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            if ((keyboard.VirtualKey == VkShift || keyboard.ScanCode is 0x2A or 0x36) &&
                keyboard.VirtualKey == VkShift)
            {
                var now = Environment.TickCount64;
                var elapsed = now - _lastShiftUpTick;
                _lastShiftUpTick = now;

                if (elapsed is >= MinimumIntervalMs and <= MaximumIntervalMs)
                {
                    _lastShiftUpTick = 0;
                    DoubleShiftPressed?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        GC.SuppressFinalize(this);
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public int VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookId,
        LowLevelKeyboardProc callback,
        IntPtr moduleHandle,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hookHandle,
        int nCode,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
