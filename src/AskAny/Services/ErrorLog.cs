using System.IO;
using System.Text;

namespace AskAny.Services;

// 极简错误日志。
//
// 托盘常驻应用在此之前完全没有日志：抛异常只弹一个 MessageBox 就没了，事后无从查起。
// 这里把上下文和异常写进 %APPDATA%\AskAny\error.log，并在超过上限时轮转一次。
public static class ErrorLog
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();
    private static readonly string Directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AskAny");

    public static string LogPath { get; } = Path.Combine(Directory, "error.log");

    public static void Write(string context, Exception? exception = null)
    {
        try
        {
            var builder = new StringBuilder();
            builder.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                .Append("  [")
                .Append(context)
                .Append(']');

            if (exception is not null)
            {
                builder.AppendLine();
                builder.Append("  ")
                    .Append(exception.GetType().FullName)
                    .Append(": ")
                    .Append(exception.Message);

                if (!string.IsNullOrWhiteSpace(exception.StackTrace))
                {
                    builder.AppendLine();
                    builder.Append(exception.StackTrace);
                }

                if (exception.InnerException is { } inner)
                {
                    builder.AppendLine();
                    builder.Append("  └─ ")
                        .Append(inner.GetType().FullName)
                        .Append(": ")
                        .Append(inner.Message);
                }
            }

            builder.AppendLine();

            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                RotateIfNeeded();
                File.AppendAllText(LogPath, builder.ToString(), Encoding.UTF8);
            }
        }
        catch (IOException)
        {
            // 日志本身失败不能再抛出去。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (!info.Exists || info.Length < MaxBytes)
            {
                return;
            }

            var previous = LogPath + ".1";
            File.Delete(previous);
            File.Move(LogPath, previous);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
