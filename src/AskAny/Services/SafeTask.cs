namespace AskAny.Services;

// 收口 fire-and-forget 的 async 调用。
//
// 原先散落着 6 处 `_ = SomeAsync()`：一旦抛异常，异常被丢弃、没有任何提示，用户
// 只会觉得「点了没反应」或「设置好像没保存」。这里统一捕获、写日志，并可选地回报界面。
public static class SafeTask
{
    public static void Run(
        Func<Task> operation,
        string context,
        Action<string>? onError = null)
    {
        _ = RunAsync(operation, context, onError);
    }

    private static async Task RunAsync(
        Func<Task> operation,
        string context,
        Action<string>? onError)
    {
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            // 用户主动取消不算错误。
        }
        catch (Exception exception)
        {
            ErrorLog.Write(context, exception);

            if (onError is not null)
            {
                try
                {
                    onError($"{context}失败：{exception.Message}");
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
    }
}
