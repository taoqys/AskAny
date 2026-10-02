# 接入知乎开放平台（zhihu_search + zhida）方案

> 状态：**部分落地**。`zhihu_search` 已按下方方案实现为主界面的「知乎搜索」功能项
> （只用知乎做检索，回答仍交给当前选中的模型）；**`zhida` 决定暂不接入**，
> 因此第 4.1 节的供应商能力声明与 4.2 节的 `AiService` 改造本次均未实施。
> 接口依据：知乎开放平台官方文档。文档站是 UmiJS 单页应用，`/docs` 的 HTML 没有正文，
> 正文由 `GET https://developer.zhihu.com/console/api/v3/docs` 返回（一次返回全部章节的 markdown）。
> 本文档第 2 节的事实均取自该接口原文；本地另存有完整抓取结果供比对（`artifacts/` 不入库）。

## 1. 目标与范围

| 范围 | 内容 |
| --- | --- |
| **本次目标** | 接入 `zhihu_search`（知乎站内搜索）与 `zhida`（知乎直答） |
| **后续可选** | `global_search`（全网搜索）、`hot_list`（热榜）、`knowledge_search`（知识库检索）、额度显示 |
| **明确不做** | 流式响应（当前架构为非流式）、用户数据 / 创作能力类接口、PDF 解析与 PPT 生成 |

## 2. 官方接口事实

### 2.1 鉴权（所有接口统一）

| Header | 值 |
| --- | --- |
| `Authorization` | `Bearer <access_secret>`（个人中心获取） |
| `X-Request-Timestamp` | **秒级** Unix 时间戳，**与服务端时间相差不得超过 10 分钟** |
| `Content-Type` | `application/json` |

错误码：`0` 成功、`10001` 参数错误、`20001` 鉴权失败、`30001` 频率限制、`90001` 内部错误。

### 2.2 `zhihu_search`（知乎站内搜索）

- `GET https://developer.zhihu.com/api/v1/content/zhihu_search`
- Query：`Query`（必填）、`Count`（默认 10，**最大 10**，超出自动截断）、`SortBy`（如 `VoteUpCount:desc:(10,100)`）
- 响应是 **PascalCase 自有信封**，不是 OpenAI 形状：

```json
{ "Code": 0, "Message": "success",
  "Data": { "HasMore": false, "SearchHashId": "...", "Items": [
    { "Title": "...", "ContentType": "Article", "ContentText": "摘要",
      "Url": "https://...?utm_medium=openapi_platform", "VoteUpCount": 128,
      "CommentCount": 15, "AuthorName": "张三", "EditTime": 1710000000,
      "AuthorityLevel": "2", "RankingScore": 0.98 } ] } }
```

字段：`Title`、`ContentType`、`ContentID`、`ContentText`、`Url`、`CommentCount`、`VoteUpCount`、`AuthorName`、`AuthorAvatar`、`AuthorBadge(Text)`、`EditTime`、`CommentInfoList`、`AuthorityLevel`、`RankingScore`。

### 2.3 `zhida`（知乎直答）

- `POST https://developer.zhihu.com/v1/chat/completions` — **标准 OpenAI 兼容路径**
- Body 官方**只保证** `model`、`messages`、`stream` 三个字段；**其它字段不保证生效**
- 模型档位：`zhida-fast-1p5`（快速）、`zhida-thinking-1p5`（深度思考）、`zhida-agent`（智能思考）
- `messages[].content` 是 **String**（不支持图片）
- 响应是标准 `chat.completion`，且带 `reasoning_content`：

```json
{ "choices": [ { "message": {
      "role": "assistant", "reasoning_content": "先给出分析过程...", "content": "..." } } ] }
```

- 错误体是 OpenAI 风格 `{"error":{"message","type","param","code"}}`
- 文档注明：**支持 role/content 上下文传参的模型**为 `zhida-fast-1p5`、`zhida-thinking-1p5` —— **`zhida-agent` 是否支持多轮存疑，需实测**

### 2.4 计费与额度

- 单价（含税，不足千次按实际付费次数）：知乎搜索 **20 元/千次**、直答 Agent **20 元/千次**
- 有**每日免费额度**，按北京时间 00:00 重置，不结转；同一账号多个 Access Secret 共用额度
- 额度查询：`GET /api/v1/quota?APIIDs=zhihu_search,zhida_openai`（不消耗业务额度）

## 3. 现状盘点（为什么不能"加个预设"就完事）

好消息：**`zhida` 的响应体已经被现有代码读懂了。**

| 位置 | 现状 | 对直答是否可用 |
| --- | --- | --- |
| `AiService.BuildEndpoint` | BaseUri 末尾拼 `/chat/completions` | ✅ `https://developer.zhihu.com/v1` → 正确路径 |
| `AiService.ParseChatCompletions` | 读 `choices[0].message.content` + `reasoning_content` | ✅ 完全匹配 |
| `AiService` 鉴权头 | 只设 `Authorization: Bearer` | ❌ **缺 `X-Request-Timestamp`** |
| `AiService.BuildRequestBody` | 非 Think 模式发 `temperature: 0.6`；`SupportsReasoningControl` 时发 `reasoning_effort` | ⚠️ 官方不保证，且语义不符 |
| `AiService` 消息构造 | **总是**插入 `system` 角色消息 | ⚠️ 官方未声明支持 |
| `SearchService` | 硬编码 Tavily 端点 + Tavily JSON 形状 | ❌ 与知乎完全不同，无法复用 |
| `FunctionOption.Mode` | `TrackNews` / `ExplainOnline` 触发搜索 | ⚠️ 需要改为可指定后端 |

### 三个硬障碍

**A. 缺时间戳头（阻断级）** — `AiService.ExecuteAsync` 只设了 `Authorization`，直答会直接返回 `20001`。

**B. `temperature` / `reasoning_effort` 语义不符** — 直答的推理档位由**模型选择**决定（fast vs thinking），不是靠 effort 参数。现在的 `SupportsReasoningControl` + `ReasoningEffort` 模型对直答不成立。虽然多发字段大概率被忽略，但属于依赖未声明的行为。

**C. `system` 角色未验证** — AskAny 的每个功能选项都靠系统提示词驱动（「你是一位善于表达的中文教师…」）。如果直答只接受 `user`/`assistant`，所有功能的提示词都会失效甚至报 `10001`。

## 4. 方案

### 4.1 供应商能力声明（替代继续堆布尔）

现在 `ProviderConfig` 已有 `SupportsReasoningControl`、`VisionModelsConfigured` 这类开关。再堆四个布尔会失控，因此引入 **`PresetId` + 由预设派生的能力**：

`ProviderConfig` 新增：

| 字段 | 默认 | 作用 |
| --- | --- | --- |
| `PresetId` | `"custom"` | 标识预设来源，便于迁移与显示 |
| `RequiresRequestTimestamp` | `false` | 直答 = `true`，请求时注入 `X-Request-Timestamp` |
| `SupportsTemperature` | `true` | 直答 = `false`，不发 `temperature` |
| `SupportsReasoningEffort` | `true` | 直答 = `false`，不发 `reasoning_effort` |
| `SupportsSystemRole` | `true` | 直答先设 `false`，系统提示词并入首条 user 消息 |

默认值全部保持现有行为，**老配置零影响**。

### 4.2 `AiService` 改造（4 处，均为向后兼容）

1. `ExecuteAsync`：`RequiresRequestTimestamp` 为真时加 `X-Request-Timestamp`（`DateTimeOffset.UtcNow.ToUnixTimeSeconds()`）
2. `BuildRequestBody`：`temperature` 受 `SupportsTemperature` 约束；`reasoning_effort` 受 `SupportsReasoningEffort` 约束
3. `BuildSystemPrompt` 的结果：`SupportsSystemRole` 为假时不插入 `system` 消息，改为把系统提示词 + 换行 + 用户问题合并成首条 `user` 消息
4. 错误处理：解析 `{"error":{"message"}}` 取 `message`；`20001` 时提示区分**时钟偏差**（时间戳超 10 分钟）与**密钥无效**——这是直答最常见的两个 20001 成因

响应解析与端点拼接**不动**。

### 4.3 检索后端抽象

`SearchService` 拆成接口 + 两个实现：

```
ISearchProvider.SearchAsync(query, options, key, ct) -> SearchPacket
├─ TavilySearchProvider   （现有逻辑原样迁移，行为不变）
└─ ZhihuSearchProvider    （新增：GET zhihu_search，注入时间戳头，解析 PascalCase 信封）
```

`SearchSource` 扩展字段以承载知乎的排序信号：`Author`、`VoteUpCount`、`CommentCount`、`AuthorityLevel`（Tavily 侧留空）。

按你的选择，**检索后端由每个功能选项各自指定**：

`FunctionOption` 新增 `SearchBackend`（`None` / `Tavily` / `Zhihu`）。

**迁移要点（容易踩坑）**：老配置的 JSON 里没有这个字段，反序列化会落到枚举默认值，无法区分"用户显式选了无检索"和"字段根本不存在"。若直接落默认值，现有用户的「新闻追踪」「联网解释」会**静默失去检索能力**。

因此沿用本仓库已有的同款模式——`ProviderConfig.VisionModelsConfigured` 就是为解决同一问题而设——再加一个伴随标志：

| 字段 | 作用 |
| --- | --- |
| `SearchBackend` | `None` / `Tavily` / `Zhihu` |
| `SearchBackendConfigured` | `false` 表示老配置，需按 `Mode` 推导：`TrackNews` / `ExplainOnline` → `Tavily`，其余 `None`；推导后置 `true` |

这样老配置行为完全不变，新配置可显式选择「无检索」。

### 4.4 配置与设置界面

- `AppConfig` 新增 `ZhihuAccessSecretProtected`（沿用 DPAPI 加密，与 API Key / Tavily Key 同一套 `Protect`/`Unprotect`）
- `ProviderCatalog` 新增 `"zhihu-zhida"` 预设：BaseUri `https://developer.zhihu.com/v1`、协议 Chat Completions、模型三个档位、`VisionModels` 空、能力开关按 4.1
- 设置「推理与搜索」页：加知乎 Access Secret 输入 + 测试按钮
- 设置「功能选项」编辑区：加「检索后端」下拉
- 底部模型下拉会自动出现「知乎直答 · zhida-thinking-1p5」

图片能力：`zhida` 的 `content` 是 String，**不支持图片**。把预设的 `VisionModels` 设为空即可——现有 `SupportsVisionModel` 守卫已经会在带图时明确拒绝，不会静默发文字。

## 5. 需要你确认的设计决策

| # | 问题 | 选项 | 我的建议 |
| --- | --- | --- | --- |
| 1 | 直答的「深度思考」怎么体现？直答的推理由**模型档位**决定，而 AskAny 现在用 `reasoning_effort` | (a) `zhida-thinking-1p5` 当普通模型，用户手动在底部切 (b) 功能选项加「模型档位映射」：深度思考→thinking，其余→fast (c) 供应商默认模型 + 模式覆盖 | **(b)** —— 与现有「功能决定行为」的交互一致；但先做 (a) 打通链路，再上 (b) |
| 2 | 系统提示词怎么办？ | (a) 并入首条 user 消息（不依赖 system 角色） (b) 直接发 system，实测是否被拒 | **(a)**，实测确认支持后再放开 (b) |
| 3 | 是否一并接入 `global_search`（全网搜索）？ | 形状与 `zhihu_search` 几乎一致，边际成本很低 | 可放阶段 4 |
| 4 | 是否在设置里显示剩余额度？ | 调 `/api/v1/quota` | 建议做，直答是计费的 |

## 6. 实施步骤（每阶段可独立验证）

| 阶段 | 内容 | 风险 |
| --- | --- | --- |
| **1** | 能力声明字段 + `AiService` 四处兼容改造 + `zhihu-zhida` 预设 | 低，且不改变任何现有供应商行为 |
| **2** | `ISearchProvider` 抽象 + Tavily 迁移（**纯重构，行为不变**） | 低，靠现有 Tavily 用法回归 |
| **3** | `ZhihuSearchProvider` + `FunctionOption.SearchBackend` + 设置界面接线 + 配置迁移 | 中 |
| **4** | 额度显示、`global_search`、热榜、动态「模型档位映射」 | 低 |

阶段 2 独立出来的价值：先做无行为变化的抽象，确认 Tavily 没退化，再叠加知乎实现，出问题时能定位到具体一层。

## 7. 验证方案

CI **无法覆盖**这部分——必须用真实 Access Secret 打真接口，且需要你提供密钥。

要验的断言：

1. 直答：`X-Request-Timestamp` 加上后不再出现 `20001`；`reasoning_content` 正确落进「思考过程」折叠区
2. 系统提示词并入 user 消息后，功能选项的提示词语义仍然生效（用「解释说明」对比回答风格）
3. 知乎搜索：来源标注 `[1][2]` 正常，`Url` 可直接打开，`VoteUpCount`/`AuthorName` 正确显示
4. 无结果时 `EmptyReason` 的提示是否友好（现在 `AppendSources` 对空来源的处理需要顺带核对）
5. **时钟偏差**：把本机时间调偏 11 分钟，确认错误提示指向时间戳而非密钥
6. `zhida-agent` 多轮上下文是否可用（文档未承诺，需实测）
7. 知乎搜索 `Count` 上限 10 —— 现在 Tavily 用 6，确认参数映射

## 8. 风险

| 风险 | 说明 | 应对 |
| --- | --- | --- |
| 官方只保证 3 个请求字段 | 依赖 `temperature` 等属于未声明行为 | 能力开关默认关掉，不依赖 |
| `system` 角色未声明 | 可能全盘影响提示词 | 默认走并入 user 消息 |
| `zhida-agent` 上下文存疑 | 多轮可能失效 | 实测；必要时对 agent 禁用追问 |
| 计费 | 搜索 20 元/千次、Agent 20 元/千次 | 界面提示 + 额度查询；仅用户主动触发才调用 |
| 时钟要求 10 分钟内 | 本机时间不准会全线 20001 | 错误提示区分成因；必要时提示校准时间 |
| `EditTime` 是秒级时间戳 | 与 Tavily 的 `published_date` 字符串不同 | 统一在 `SearchSource` 层归一化为 ISO 字符串 |

## 9. 附：本次未采用的替代路径

官方同时提供 **Zhihu CLI**、**MCP Server**（`unified_mcp`）和 **Skill 包**（zip 下载）。不采用的原因：AskAny 目前没有 MCP 客户端，也没有 Agent Skill 运行时；直接走 HTTP 只需改动 `AiService` 与检索层，引入全新运行时协议的成本远高于收益。若后续要接知识库、PDF 解析等能力，再评估 MCP。
