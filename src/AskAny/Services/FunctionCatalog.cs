using AskAny.Models;

namespace AskAny.Services;

public static class FunctionCatalog
{
    public static List<FunctionOption> CreateDefaultFunctions()
    {
        return
        [
            Create(
                "answer",
                WorkflowMode.Answer,
                "回答此问题",
                "直接、准确地解答当前问题",
                "\uE8BD",
                "你是一个严谨、直接的中文 AI 助手。优先给出可执行、准确、简洁的回答。",
                0,
                -3.5),
            Create(
                "explain",
                WorkflowMode.Explain,
                "解释说明",
                "拆解概念、背景和关键要点",
                "\uE946",
                "你是一位善于表达的中文教师。用清晰、通俗的方式解释概念，按必要性给出定义、背景、例子和易错点。",
                0.5,
                -2.5),
            Create(
                "news",
                WorkflowMode.TrackNews,
                "新闻追踪",
                "搜索最近动态并整理事件脉络",
                "\uE909",
                "你是中文新闻分析助手。根据提供的最新检索资料整理事件动态，按重要性组织内容，区分事实、背景和可能影响。",
                0,
                -2.5),
            Create(
                "think",
                WorkflowMode.Think,
                "深度思考",
                "仅此模式请求模型的扩展推理",
                "\uE735",
                "你是严谨的中文分析助手。给出必要的分析要点，再明确写出最终结论。不要虚构不确定信息。",
                0,
                -3.5),
            Create(
                "explain-online",
                WorkflowMode.ExplainOnline,
                "联网解释",
                "结合网络资料解释问题并标注来源",
                "\uE774",
                "你是中文研究型解释助手。结合提供的联网检索资料解释问题，明确区分资料事实、推断和你的结论。",
                0,
                -2.5)
        ];
    }

    public static FunctionOption CreateCustom()
    {
        return Create(
            Guid.NewGuid().ToString("N"),
            WorkflowMode.Answer,
            "自定义选项",
            "使用自定义提示词回答问题",
            "\uE8BD",
            GetDefaultSystemPrompt(WorkflowMode.Answer));
    }

    public static string GetDefaultSystemPrompt(WorkflowMode mode)
    {
        return mode switch
        {
            WorkflowMode.Answer =>
                "你是一个严谨、直接的中文 AI 助手。优先给出可执行、准确、简洁的回答。",
            WorkflowMode.Explain =>
                "你是一位善于表达的中文教师。用清晰、通俗的方式解释概念，按必要性给出定义、背景、例子和易错点。",
            WorkflowMode.TrackNews =>
                "你是中文新闻分析助手。根据提供的最新检索资料整理事件动态，按重要性组织内容，区分事实、背景和可能影响。",
            WorkflowMode.Think =>
                "你是严谨的中文分析助手。给出必要的分析要点，再明确写出最终结论。不要虚构不确定信息。",
            WorkflowMode.ExplainOnline =>
                "你是中文研究型解释助手。结合提供的联网检索资料解释问题，明确区分资料事实、推断和你的结论。",
            _ => "你是一个中文 AI 助手。"
        };
    }

    public static string GetModeName(WorkflowMode mode)
    {
        return mode switch
        {
            WorkflowMode.Answer => "标准回答",
            WorkflowMode.Explain => "解释说明",
            WorkflowMode.TrackNews => "新闻追踪（联网）",
            WorkflowMode.Think => "深度思考（启用推理）",
            WorkflowMode.ExplainOnline => "联网解释（联网）",
            _ => "标准回答"
        };
    }

    private static FunctionOption Create(
        string id,
        WorkflowMode mode,
        string name,
        string description,
        string glyph,
        string systemPrompt,
        double offsetX = 0,
        double offsetY = 0)
    {
        return new FunctionOption
        {
            Id = id,
            Mode = mode,
            Name = name,
            Description = description,
            Glyph = glyph,
            SystemPrompt = systemPrompt,
            IconOffsetX = offsetX,
            IconOffsetY = offsetY
        };
    }
}
