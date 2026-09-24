using System.Windows.Automation;

namespace AskAny.Services;

public static class SelectionCaptureService
{
    public static string? TryCapture()
    {
        try
        {
            var focusedElement = AutomationElement.FocusedElement;
            if (focusedElement is null ||
                !focusedElement.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) ||
                patternObject is not TextPattern textPattern)
            {
                return null;
            }

            var selections = textPattern.GetSelection()
                .Select(range => range.GetText(-1).Trim())
                .Where(text => !string.IsNullOrWhiteSpace(text));
            var selectedText = string.Join(Environment.NewLine, selections).Trim();
            return string.IsNullOrWhiteSpace(selectedText) ? null : selectedText;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
