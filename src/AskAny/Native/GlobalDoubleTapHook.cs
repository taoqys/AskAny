using System.Diagnostics;
using System.Runtime.InteropServices;
using AskAny.Models;

namespace AskAny.Native;

// 监听全局低级键盘事件，识别「双击某个修饰键」的手势。
// 钩子只旁听、不拦截按键，所以不会像系统热键注册那样把组合键从其它程序手里抢走。
//
// 修饰键是双刃剑：Ctrl+C / Ctrl+V 连打会产生两次 Ctrl 抬起，若不加判断就会被误认成
// 双击。规则（对 Shift / Ctrl / Alt 一视同仁）：
//   一次按住期间只要出现过别的按键（或按下时已按住别的修饰键），这次抬起就不算独立单击，
//   并且会清空已积累的配对，避免和后面的单击连成一对。
//
// 旧版把 Shift 当成「没有组合键风险」而只对 Ctrl 做这套判断，其实 Shift 也一样会误触：
// 连续两次 Shift+字母 会给出两次 Shift 抬起。这里统一处理。
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

    private const int MinimumIntervalMs = 70;
    private const int MaximumIntervalMs = 550;
    private const int DefaultIntervalMs = 400;

    private readonly LowLevelKeyboardProc _hookProc;
    private readonly ModifierState[] _states = [new(), new(), new()];
    private readonly bool[] _held = new bool[3];

    private IntPtr _hook = IntPtr.Zero;
    private int _intervalMs = DefaultIntervalMs;

    public GlobalDoubleTapHook()
    {
        _hookProc = HookCallback;
    }

    // 参数是「被双击的修饰键」，由上层决定它对应什么动作。
    public event EventHandler<HotkeyModifier>? DoubleTap;

    // 双击间隔上限，超出 [70, 550] 会被夹到边界。
    public int IntervalMs
    {
        get => _intervalMs;
        set => _intervalMs = Math.Clamp(value, MinimumIntervalMs, MaximumIntervalMs);
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

    // 低级键盘钩子的回调一旦超时，系统会直接把钩子摘掉（表现为双击突然失效），
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

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        GC.SuppressFinalize(this);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var keyboard = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            var virtualKey = keyboard.VirtualKey;
            var isKeyUp = wParam.ToInt64() == WmKeyUp || (keyboard.Flags & LlkfUp) != 0;

            if (TryGetModifier(virtualKey, out var modifier))
            {
                if (isKeyUp)
                {
                    HandleModifierUp(modifier);
                }
                else
                {
                    HandleModifierDown(modifier);
                }
            }
            else if (!isKeyUp)
            {
                // 出现普通按键（或 Win 键）：正按住的修饰键都失去「独立单击」资格，
                // 并断开已积累的配对。
                for (var index = 0; index < _states.Length; index++)
                {
                    if (_held[index])
                    {
                        _states[index].Clean = false;
                    }

                    ResetPair(index);
                }
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void HandleModifierDown(HotkeyModifier modifier)
    {
        var index = (int)modifier;
        _held[index] = true;

        // 按下时若已按住别的修饰键（例如先按 Ctrl 再按 Shift），这次不算独立单击。
        var otherHeld = false;
        for (var probe = 0; probe < _held.Length; probe++)
        {
            if (probe != index && _held[probe])
            {
                otherHeld = true;
                break;
            }
        }

        _states[index].Clean = !otherHeld;
    }

    private void HandleModifierUp(HotkeyModifier modifier)
    {
        var index = (int)modifier;
        var state = _states[index];
        _held[index] = false;

        if (!state.Clean)
        {
            ResetPair(index);
            return;
        }

        var now = Environment.TickCount64;
        var elapsed = now - state.LastUpTick;

        if (state.Pending && elapsed >= MinimumIntervalMs && elapsed <= IntervalMs)
        {
            ResetPair(index);
            DoubleTap?.Invoke(this, modifier);
            return;
        }

        // 记录为待配对的第一次单击。
        state.Pending = true;
        state.LastUpTick = now;
    }

    private void ResetPair(int index)
    {
        _states[index].Pending = false;
        _states[index].LastUpTick = 0;
    }

    private static bool TryGetModifier(int virtualKey, out HotkeyModifier modifier)
    {
        if (IsShiftKey(virtualKey))
        {
            modifier = HotkeyModifier.Shift;
            return true;
        }

        if (IsControlKey(virtualKey))
        {
            modifier = HotkeyModifier.Control;
            return true;
        }

        if (IsAltKey(virtualKey))
        {
            modifier = HotkeyModifier.Alt;
            return true;
        }

        modifier = default;
        return false;
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

    private sealed class ModifierState
    {
        public long LastUpTick { get; set; }

        public bool Pending { get; set; }

        public bool Clean { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public int VirtualKey;
        public int ScanCode;
        public uint Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelKeyboardProc lpfn,
        IntPtr hMod,
        uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
