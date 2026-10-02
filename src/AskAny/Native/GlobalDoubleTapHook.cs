using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AskAny.Native;

// 监听全局低级键盘事件，识别“双击 Shift”和“双击 Ctrl”两种手势。
// 钩子只旁听、不拦截按键，所以不会像系统热键注册那样把组合键从其它程序手里抢走。
//
// Ctrl 是修饰键，必须区分“独立单击”和“组合键的一部分”：
// Ctrl+C、Ctrl+V 连打会产生两次 Ctrl 抬起，若不加判断就会被误认成双击 Ctrl。
// 规则：一次 Ctrl 按住期间只要出现过其它按键（或按下时已按住其它修饰键），
// 这次 Ctrl 抬起就不算独立单击，并会清空已积累的配对，避免和后面的单击连成一对。
public sealed class GlobalDoubleTapHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyUp = 0x0101;
    private const uint LlkfUp = 0x80;
    private const int VkShift = 0x10;
    private const int VkLeftShift = 0xA0;
    private const int VkRightShift = 0xA1;
    private const int VkControl = 0x11;
    private const int VkLeftControl = 0xA2;
    private const int VkRightControl = 0xA3;
    private const int VkMenu = 0x12;
    private const int VkLeftMenu = 0xA4;
    private const int VkRightMenu = 0xA5;
    private const int VkLeftWindows = 0x5B;
    private const int VkRightWindows = 0x5C;
    private const long MinimumIntervalMs = 70;
    private const long MaximumIntervalMs = 550;

    private readonly LowLevelKeyboardProc _hookProc;
    private IntPtr _hook = IntPtr.Zero;

    private long _lastShiftUpTick;

    private long _lastCtrlUpTick;
    private bool _ctrlTapClean;
    private bool _ctrlTapPending;
    private bool _shiftHeld;
    private bool _altHeld;
    private bool _windowsHeld;

    public event EventHandler? DoubleShiftPressed;

    public event EventHandler? DoubleCtrlPressed;

    public GlobalDoubleTapHook()
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

    public bool IsInstalled => _hook != IntPtr.Zero;

    // 低级键盘钩子的回调一旦超时，系统会直接把钩子摘掉（表现为双击 Shift 突然失效），
    // 而且不会通知我们。这里提供重装入口，由上层定期调用以自愈。
    public void Reinstall()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        Install();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var keyboard = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            var virtualKey = keyboard.VirtualKey;
            var isKeyUp = wParam.ToInt64() == WmKeyUp || (keyboard.Flags & LlkfUp) != 0;

            TrackModifiers(virtualKey, isKeyUp);

            if (isKeyUp)
            {
                if (IsShiftKey(virtualKey))
                {
                    HandleShiftKeyUp();
                }
                else if (IsControlKey(virtualKey))
                {
                    HandleControlKeyUp();
                }
            }
            else if (IsControlKey(virtualKey))
            {
                // Ctrl 按下时若已按住其它修饰键（例如 Ctrl+Shift），这次按下不算独立单击。
                _ctrlTapClean = !_shiftHeld && !_altHeld && !_windowsHeld;
            }
            else if (IsOtherModifierKey(virtualKey))
            {
                // 组合键期间按下的其它修饰键会让当前 Ctrl 手势失去资格。
                _ctrlTapClean = false;
            }
            else
            {
                // 出现普通按键，说明这次 Ctrl 是组合键的一部分；同时断开已积累的配对。
                _ctrlTapClean = false;
                ResetControlPair();
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void HandleShiftKeyUp()
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

    private void HandleControlKeyUp()
    {
        if (!_ctrlTapClean)
        {
            ResetControlPair();
            return;
        }

        var now = Environment.TickCount64;
        if (_ctrlTapPending &&
            now - _lastCtrlUpTick is >= MinimumIntervalMs and <= MaximumIntervalMs)
        {
            ResetControlPair();
            DoubleCtrlPressed?.Invoke(this, EventArgs.Empty);
            return;
        }

        // 记录为待配对的第一次单击。
        _ctrlTapPending = true;
        _lastCtrlUpTick = now;
    }

    private void ResetControlPair()
    {
        _ctrlTapPending = false;
        _lastCtrlUpTick = 0;
    }

    private void TrackModifiers(int virtualKey, bool isKeyUp)
    {
        if (IsShiftKey(virtualKey))
        {
            _shiftHeld = !isKeyUp;
        }
        else if (IsAltKey(virtualKey))
        {
            _altHeld = !isKeyUp;
        }
        else if (IsWindowsKey(virtualKey))
        {
            _windowsHeld = !isKeyUp;
        }
    }

    private static bool IsShiftKey(int virtualKey)
    {
        return virtualKey is VkShift or VkLeftShift or VkRightShift;
    }

    private static bool IsControlKey(int virtualKey)
    {
        return virtualKey is VkControl or VkLeftControl or VkRightControl;
    }

    private static bool IsAltKey(int virtualKey)
    {
        return virtualKey is VkMenu or VkLeftMenu or VkRightMenu;
    }

    private static bool IsWindowsKey(int virtualKey)
    {
        return virtualKey is VkLeftWindows or VkRightWindows;
    }

    private static bool IsOtherModifierKey(int virtualKey)
    {
        return IsShiftKey(virtualKey) || IsAltKey(virtualKey) || IsWindowsKey(virtualKey);
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
