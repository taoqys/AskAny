using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using AskAny.Models;
using AskAny.Services;

namespace AskAny.Native;

public static class ScreenCaptureService
{
    public static ImageAttachment? CaptureRegion()
    {
        using var screenshot = CaptureCurrentScreen();
        using var overlay = new ScreenshotOverlayForm(screenshot);
        return overlay.ShowDialog() == DialogResult.OK && overlay.Result is { } result
            ? ImageAttachmentService.FromBitmap(
                result,
                $"截图-{DateTime.Now:yyyyMMdd-HHmmss}.png",
                preferPng: true)
            : null;
    }

    private static Bitmap CaptureCurrentScreen()
    {
        var bounds = Screen.FromPoint(Cursor.Position).Bounds;
        var bitmap = new Bitmap(
            bounds.Width,
            bounds.Height,
            PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(
            bounds.Location,
            Point.Empty,
            bounds.Size,
            CopyPixelOperation.SourceCopy);
        return bitmap;
    }
}

internal sealed class ScreenshotOverlayForm : Form
{
    private readonly Bitmap _screenshot;
    private Point _startPoint;
    private Rectangle _selection;
    private bool _isDragging;

    public ScreenshotOverlayForm(Bitmap screenshot)
    {
        _screenshot = screenshot;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Black;
        ClientSize = screenshot.Size;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        FormBorderStyle = FormBorderStyle.None;
        KeyPreview = true;
        Location = Screen.FromPoint(Cursor.Position).Bounds.Location;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
    }

    public Bitmap? Result { get; private set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.DrawImageUnscaled(_screenshot, Point.Empty);

        using var dim = new SolidBrush(Color.FromArgb(135, 0, 0, 0));
        e.Graphics.FillRectangle(dim, ClientRectangle);

        if (_selection.Width <= 1 || _selection.Height <= 1)
        {
            return;
        }

        var state = e.Graphics.Save();
        e.Graphics.SetClip(_selection);
        e.Graphics.DrawImageUnscaled(_screenshot, Point.Empty);
        e.Graphics.Restore(state);

        using var borderPen = new Pen(Color.FromArgb(40, 104, 232), 2);
        e.Graphics.DrawRectangle(
            borderPen,
            _selection.Left,
            _selection.Top,
            Math.Max(0, _selection.Width - 1),
            Math.Max(0, _selection.Height - 1));

        var sizeText = $"{_selection.Width} × {_selection.Height}";
        using var background = new SolidBrush(Color.FromArgb(220, 32, 33, 36));
        using var textBrush = new SolidBrush(Color.White);
        using var font = new Font("Microsoft YaHei UI", 9f);
        var textSize = e.Graphics.MeasureString(sizeText, font);
        var textLeft = Math.Min(
            Math.Max(0, _selection.Left),
            Math.Max(0, ClientSize.Width - (int)textSize.Width - 12));
        var textTop = Math.Max(0, _selection.Top - (int)textSize.Height - 10);
        var textBounds = new RectangleF(
            textLeft,
            textTop,
            textSize.Width + 12,
            textSize.Height + 6);
        e.Graphics.FillRectangle(background, textBounds);
        e.Graphics.DrawString(
            sizeText,
            font,
            textBrush,
            textLeft + 6,
            textTop + 3);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        _startPoint = e.Location;
        _selection = new Rectangle(e.Location, Size.Empty);
        _isDragging = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_isDragging)
        {
            return;
        }

        _selection = CreateSelection(_startPoint, e.Location);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || !_isDragging)
        {
            return;
        }

        _isDragging = false;
        _selection = CreateSelection(_startPoint, e.Location);
        if (_selection.Width < 4 || _selection.Height < 4)
        {
            DialogResult = DialogResult.Cancel;
            Close();
            return;
        }

        Result = _screenshot.Clone(_selection, PixelFormat.Format32bppArgb);
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode != Keys.Escape)
        {
            return;
        }

        DialogResult = DialogResult.Cancel;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Result?.Dispose();
        }

        base.Dispose(disposing);
    }

    private Rectangle CreateSelection(Point start, Point end)
    {
        var left = Math.Clamp(Math.Min(start.X, end.X), 0, ClientSize.Width);
        var top = Math.Clamp(Math.Min(start.Y, end.Y), 0, ClientSize.Height);
        var right = Math.Clamp(Math.Max(start.X, end.X), 0, ClientSize.Width);
        var bottom = Math.Clamp(Math.Max(start.Y, end.Y), 0, ClientSize.Height);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }
}
