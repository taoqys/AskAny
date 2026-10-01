using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;

namespace AskAny.Services;

// 选区捕获。
//
// 原实现只认「焦点元素自身」支持 TextPattern，而现代 SPA（X / 知乎等）的焦点会落在
// 内层 div 上（ControlType.Group），它并不支持 TextPattern —— 实测在 X 上向上 8 层
// 祖先全部不支持，因此这些站点永远返回 null，从来不会自动填充。
//
// 现在按三层依次尝试，最后一层是浏览器专用的剪贴板兜底：
//   1. 焦点元素自身 → 祖先（原生控件第 0 层命中，行为与原来一致）
//   2. 前台窗口内的 Document（浏览器把页面内容挂在这里）
//   3. 对浏览器窗口模拟 Ctrl+C
// 第 2、3 层都用 Win32 的 GetForegroundWindow 定位窗口，而不是靠 UIA 往上找 Window ——
// 实测在 Chrome 里沿 UIA 父链并不总能命中 ControlType.Window，会把兜底整条堵死。
public static class SelectionCaptureService
{
    private const int MaxAncestorHops = 64;
    private const int ClipboardWaitMilliseconds = 250;
    private const int ClipboardPollMilliseconds = 10;

    private const byte VkControl = 0x11;
    private const byte VkC = 0x43;
    private const uint KeyEventKeyUp = 0x0002;

    public static string? TryCapture()
    {
        // 1) 焦点元素自身 → 祖先。
        var focused = TryGetFocusedElement();
        if (focused is not null)
        {
            var fromAncestors = TryReadFromAncestors(focused);
            if (!string.IsNullOrWhiteSpace(fromAncestors))
            {
                return fromAncestors;
            }
        }

        var foreground = GetForegroundWindow();

        // 2) 前台窗口内的 Document。
        var window = TryGetElementFromHandle(foreground);
        if (window is not null)
        {
            var fromDocuments = TryReadFromDocuments(window);
            if (!string.IsNullOrWhiteSpace(fromDocuments))
            {
                return fromDocuments;
            }
        }

        // 3) 兜底：模拟 Ctrl+C。只对浏览器类窗口启用 —— Excel 这类程序在没有选区时
        //    按 Ctrl+C 也会复制当前单元格，会把无关内容填进输入框。
        return IsBrowserWindow(foreground) ? TryReadFromClipboard() : null;
    }

    private static AutomationElement? TryGetFocusedElement()
    {
        try
        {
            return AutomationElement.FocusedElement;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static AutomationElement? TryGetElementFromHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return AutomationElement.FromHandle(handle);
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static string? TryReadFromAncestors(AutomationElement start)
    {
        var walker = TreeWalker.ControlViewWalker;
        var node = start;

        for (var hop = 0; hop <= MaxAncestorHops && node is not null; hop++)
        {
            var text = TryReadSelection(node);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            try
            {
                node = walker.GetParent(node);
            }
            catch (ElementNotAvailableException)
            {
                break;
            }
        }

        return null;
    }

    private static string? TryReadFromDocuments(AutomationElement window)
    {
        try
        {
            var condition = new PropertyCondition(
                AutomationElement.ControlTypeProperty,
                ControlType.Document);

            foreach (AutomationElement document in window.FindAll(TreeScope.Descendants, condition))
            {
                var text = TryReadSelection(document);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        return null;
    }

    private static string? TryReadSelection(AutomationElement element)
    {
        try
        {
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) ||
                pattern is not TextPattern textPattern)
            {
                return null;
            }

            var parts = textPattern.GetSelection()
                .Select(range => range.GetText(-1))
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text.Trim())
                .ToArray();

            return parts.Length == 0 ? null : string.Join(Environment.NewLine, parts);
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool IsBrowserWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var buffer = new StringBuilder(256);
            if (GetClassName(handle, buffer, buffer.Capacity) == 0)
            {
                return false;
            }

            var className = buffer.ToString();
            return className.StartsWith("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase) ||
                   className.Equals("MozillaWindowClass", StringComparison.OrdinalIgnoreCase);
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static string? TryReadFromClipboard()
    {
        try
        {
            var sequenceBefore = GetClipboardSequenceNumber();
            var snapshot = TrySnapshotClipboard();

            SendCtrlC();

            var deadline = Environment.TickCount64 + ClipboardWaitMilliseconds;
            while (GetClipboardSequenceNumber() == sequenceBefore &&
                   Environment.TickCount64 < deadline)
            {
                Thread.Sleep(ClipboardPollMilliseconds);
            }

            // 序列号没变 → 这次 Ctrl+C 什么都没复制（没有选区）。
            // 此时绝不能把剪贴板里的旧内容当成本次选区返回。
            if (GetClipboardSequenceNumber() == sequenceBefore || !Clipboard.ContainsText())
            {
                return null;
            }

            var text = Clipboard.GetText();
            RestoreClipboard(snapshot);
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (COMException)
        {
            return null;
        }
        catch (ExternalException)
        {
            return null;
        }
    }

    // 兜底路径不该把用户剪贴板里的东西冲掉，尽量还原。
    private static IDataObject? TrySnapshotClipboard()
    {
        try
        {
            return Clipboard.GetDataObject();
        }
        catch (COMException)
        {
            return null;
        }
        catch (ExternalException)
        {
            return null;
        }
    }

    private static void RestoreClipboard(IDataObject? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        try
        {
            Clipboard.SetDataObject(snapshot, true);
        }
        catch (COMException)
        {
        }
        catch (ExternalException)
        {
        }
    }

    private static void SendCtrlC()
    {
        keybd_event(VkControl, 0, 0, 0);
        keybd_event(VkC, 0, 0, 0);
        keybd_event(VkC, 0, KeyEventKeyUp, 0);
        keybd_event(VkControl, 0, KeyEventKeyUp, 0);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr windowHandle, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, nuint extraInfo);
}
