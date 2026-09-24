# AskAny

AskAny 是一个使用 WPF 和 .NET 8 构建的 Windows AI 快捷助手。双击 `Shift` 即可在当前屏幕位置弹出面板，通过键盘选择功能并立即提交问题。

## 功能

- 双击左或右 `Shift` 唤起/聚焦悬浮窗
- `↑` / `↓` 选择功能，`Enter` 提交，`Esc` 隐藏，`Ctrl + Enter` 也可提交
- 回答问题、解释说明、新闻追踪、深度思考、联网解释
- 支持任意 OpenAI Chat Completions 兼容接口
- 支持 Tavily Search API，用于新闻追踪和联网解释
- 中文界面，支持固定窗口、复制回答和保存对话
- API 密钥使用 Windows DPAPI 按当前用户加密后保存

## 配置

第一次启动后点击右上角设置按钮：

1. 填写 OpenAI 兼容接口的基础地址，例如 `https://api.openai.com/v1`
2. 填写 API Key 和模型名
3. 如需“新闻追踪”或“联网解释”，填写 Tavily API Key
4. 点击“测试连接”验证模型配置，然后保存

配置保存在 `%APPDATA%\AskAny\config.json`，密钥字段使用 Windows DPAPI 加密。

## 键盘操作

| 按键 | 操作 |
| --- | --- |
| 双击 `Shift` | 唤起窗口 |
| `↑` / `↓` | 选择 AI 功能 |
| `Enter` | 执行当前功能 |
| `Ctrl + Enter` | 执行当前功能 |
| `Esc` | 隐藏窗口 |

## 构建

```powershell
dotnet restore AskAny.sln
dotnet build AskAny.sln -c Release
dotnet publish src/AskAny/AskAny.csproj -c Release -r win-x64 --self-contained false
```

GitHub Actions 会在 Windows 上完成还原、构建和发布打包。
