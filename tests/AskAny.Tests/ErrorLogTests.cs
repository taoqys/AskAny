using System;
using System.IO;
using System.Text;
using AskAny.Services;
using Xunit;

namespace AskAny.Tests;

// 日志是「出问题时唯一的线索」，所以它本身也得被测：写不进去、轮转坏了都会静默失效。
public class ErrorLogTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        "askany-errorlog-test-" + Guid.NewGuid().ToString("N") + ".log");

    public ErrorLogTests()
    {
        ErrorLog.UsePathForTesting(_path);
    }

    public void Dispose()
    {
        foreach (var candidate in new[] { _path, _path + ".1" })
        {
            if (File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }
    }

    [Fact]
    public void WriteRecordsContextAndException()
    {
        ErrorLog.Write("保存配置", new InvalidOperationException("磁盘满了"));

        var text = File.ReadAllText(_path, Encoding.UTF8);

        Assert.Contains("保存配置", text);
        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("磁盘满了", text);
    }

    [Fact]
    public void WriteAcceptsContextOnly()
    {
        ErrorLog.Write("截图预览：等待初次加载超时");

        Assert.Contains("截图预览", File.ReadAllText(_path, Encoding.UTF8));
    }

    [Fact]
    public void WriteAppendsInsteadOfOverwriting()
    {
        ErrorLog.Write("第一条");
        ErrorLog.Write("第二条");

        var text = File.ReadAllText(_path, Encoding.UTF8);

        Assert.Contains("第一条", text);
        Assert.Contains("第二条", text);
    }

    [Fact]
    public void InnerExceptionIsRecorded()
    {
        var outer = new InvalidOperationException(
            "外层",
            new TimeoutException("内层"));

        ErrorLog.Write("嵌套", outer);

        var text = File.ReadAllText(_path, Encoding.UTF8);

        Assert.Contains("外层", text);
        Assert.Contains("TimeoutException", text);
        Assert.Contains("内层", text);
    }

    [Fact]
    public void OversizedLogIsRotatedOnce()
    {
        var filler = new string('x', 120_000);

        for (var index = 0; index < 4; index++)
        {
            ErrorLog.Write("大条目", new InvalidOperationException(filler));
        }

        Assert.True(File.Exists(_path + ".1"), "超过上限后应当轮转出 .1");
    }

    // 日志写不进去也不能把调用方带崩。
    // 用一个同名文件占住目录位置，CreateDirectory 会直接失败（否则它会自动建目录，
    // 那样这条用例其实什么都没测到）。
    [Fact]
    public void UnwritablePathDoesNotThrow()
    {
        var blocker = Path.Combine(
            Path.GetTempPath(),
            "askany-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "blocker");

        try
        {
            ErrorLog.UsePathForTesting(Path.Combine(blocker, "sub", "error.log"));

            var exception = Record.Exception(() => ErrorLog.Write("路径不可写"));

            Assert.Null(exception);
        }
        finally
        {
            File.Delete(blocker);
        }
    }
}
