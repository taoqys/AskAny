# AskAny

AskAny 是一个使用 WPF 和 .NET 8 构建的 Windows AI 快捷助手。双击 `Shift` 即可弹出面板，通过键盘选择功能并立即提交问题。

## 功能

- 双击左或右 `Shift` 唤起窗口；再次双击可隐藏
- `↑` / `↓` 选择功能，`Enter` 提交，`Esc` 隐藏，`Ctrl + Enter` 也可提交
- 回答问题、解释说明、新闻追踪、深度思考、联网解释
- 支持多个 OpenAI Chat Completions / Responses API 提供商
- 内置 OpenAI、OpenAI Responses 和 DeepSeek Responses 预设
- 主窗口底部可直接切换提供商和模型，模型名称也可以手动输入
- 仅“深度思考”请求扩展推理；支持推理控制的提供商会在其他模式显式关闭推理
- 支持 Tavily Search API，用于新闻追踪和联网解释
- 自动捕获当前窗口中选中的文字，并填入问题输入框
- 窗口失去焦点后自动隐藏
- 系统托盘图标，可快速显示助手、打开历史记录和设置
- 所有提供商的模型合并为一个可搜索列表，条目显示“提供商 · 模型”
- 回答后按 `Enter` 输入追问，按 `←` 返回功能列表
- 支持多轮对话上下文，并可在设置中开关开机自动启动
- 最近 200 次请求历史记录，支持搜索、复制、删除和再次使用
- Markdown 回答渲染，支持标题、列表、引用、代码块、粗体和链接
- 中文界面，支持固定窗口、复制回答和保存 Markdown
- API 密钥使用 Windows DPAPI 按当前用户加密后保存

## 配置

第一次启动后点击右上角设置按钮，或使用任务栏托盘菜单：

1. 选择 OpenAI、DeepSeek 或自定义提供商预设
2. 填写接口地址、API Key 和模型列表
3. 选择 Chat Completions 或 Responses API 协议
4. 对支持推理控制的提供商设置推理强度
5. 如需“新闻追踪”或“联网解释”，填写 Tavily API Key
6. 点击“测试当前提供商”验证配置，然后保存

DeepSeek Responses 预设使用 `https://api.deepseek.com`，遵循 DeepSeek 的 `reasoning.effort` 规则：深度思考模式使用配置的强度，其他模式显式发送 `none`，避免默认开启思考。

配置保存在 `%APPDATA%\AskAny\config.json`，历史记录保存在 `%APPDATA%\AskAny\history.json`。密钥字段使用 Windows DPAPI 加密。

## 键盘操作

| 按键 | 操作 |
| --- | --- |
| 双击 `Shift` | 唤起窗口 |
| `↑` / `↓` | 选择 AI 功能 |
| `Enter` | 执行当前功能 |
| 回答后按 `Enter` | 输入下一轮追问 |
| 回答后按 `←` | 返回功能列表 |
| `Ctrl + Enter` | 执行当前功能 |
| `Esc` | 隐藏窗口 |

## 构建

```powershell
dotnet restore AskAny.sln
dotnet build AskAny.sln -c Release
dotnet publish src/AskAny/AskAny.csproj -c Release -r win-x64 --self-contained false
```

GitHub Actions 会在 Windows 上完成还原、构建和发布打包。
