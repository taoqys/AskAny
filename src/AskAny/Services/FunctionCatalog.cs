using AskAny.Models;

namespace AskAny.Services;

public static class FunctionCatalog
{
    // 精简后的默认功能集：三个「不检索」的意图 + 一个「联网检索」（来源在面板上切换）。
    // 原来的「联网解释 / 新闻追踪 / 知乎搜索」三项只是同一动作的不同检索来源，已合并。
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
                "think",
                WorkflowMode.Think,
                "深度思考",
                "仅此模式请求模型的扩展推理",
                "\uE735",
                "你是严谨的中文分析助手。给出必要的分析要点，再明确写出最终结论。不要虚构不确定信息。",
                0,
                -3.5),
            CreateNetwork()
        ];
    }

    public static FunctionOption CreateNetwork()
    {
        var function = Create(
            "network",
            WorkflowMode.SearchNetwork,
            "联网检索",
            "检索网络资料后回答，可切换全网 / 新闻 / 知乎",
            "\uE774",
            GetDefaultSystemPrompt(WorkflowMode.SearchNetwork),
            0,
            -2.5);
        function.SearchBackend = SearchBackend.TavilyGeneral;
        return function;
    }

    public static bool IsLegacySearchMode(WorkflowMode mode)
    {
        return mode is WorkflowMode.TrackNews
            or WorkflowMode.ExplainOnline
            or WorkflowMode.ZhihuSearch;
    }

    public static SearchBackend SearchBackendForLegacyMode(WorkflowMode mode)
    {
        return mode switch
        {
            WorkflowMode.TrackNews => SearchBackend.TavilyNews,
            WorkflowMode.ExplainOnline => SearchBackend.TavilyGeneral,
            WorkflowMode.ZhihuSearch => SearchBackend.Zhihu,
            _ => SearchBackend.None
        };
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
            WorkflowMode.ZhihuSearch =>
                "你是中文研究型助手。基于下面提供的知乎站内检索资料回答问题，" +
                "用 [1]、[2] 标注来源编号，并明确区分资料中的事实与你的推断；资料不足时直接说明。",
            WorkflowMode.SearchNetwork =>
                "你是中文研究型助手。基于下面提供的检索资料回答问题，" +
                "用 [1]、[2] 标注来源编号，并明确区分资料中的事实与你的推断；资料不足时直接说明。",
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
            WorkflowMode.ZhihuSearch => "知乎搜索（知乎站内检索）",
            WorkflowMode.SearchNetwork => "联网检索",
            _ => "标准回答"
        };
    }

    public static string GetSearchBackendName(SearchBackend source)
    {
        return source switch
        {
            SearchBackend.TavilyGeneral => "全网",
            SearchBackend.TavilyNews => "新闻",
            SearchBackend.Zhihu => "知乎",
            _ => "不检索"
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
