namespace Piable.Models;

/// <summary>用户偏好设置。以键值对形式存放于 Preferences 表。</summary>
public sealed class UserPreferences
{
    /// <summary>是否在标题栏与消息上显示统计信息。</summary>
    public bool ShowStatistics { get; set; } = true;

    /// <summary>是否显示费用。</summary>
    public bool ShowCost { get; set; } = true;

    /// <summary>是否默认展开 Token 明细（输入/输出分开显示）。</summary>
    public bool ShowDetailedTokens { get; set; }

    /// <summary>货币单位，"USD" 或 "CNY"。</summary>
    public string Currency { get; set; } = "USD";

    /// <summary>消息上的统计是否默认处于展开状态。</summary>
    public bool ExpandStatisticsByDefault { get; set; }

    /// <summary>左侧面板是否折叠。</summary>
    public bool LeftPanelCollapsed { get; set; }

    /// <summary>当前选中的供应商配置 ID。</summary>
    public string SelectedProviderId { get; set; } = string.Empty;

    /// <summary>默认智能体 ID。</summary>
    public string DefaultAgentId { get; set; } = string.Empty;

    /// <summary>主题："Light"、"Dark" 或 "System"。</summary>
    public string Theme { get; set; } = "System";

    /// <summary>界面语言。</summary>
    public string Language { get; set; } = "zh-CN";

    /// <summary>工具调用循环的最大轮数，防止无限循环。</summary>
    public int MaxToolRounds { get; set; } = 5;

    /// <summary>单次请求的超时时间（秒）。</summary>
    public int RequestTimeoutSeconds { get; set; } = 120;
}
