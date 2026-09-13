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

    /// <summary>
    /// 主窗口的原生背景模糊。取值见 <see cref="Piable.ViewModels.PreferencesViewModel"/> 的模糊模式常量：
    /// "Off" 关闭；"Mica" 走系统云母（仅 Windows 11）；"AcrylicBlur" 走亚克力（Windows 10 1803+）。
    /// 实际生效层级由操作系统决定，平台不支持时会自动退回 AcrylicBlur、再退回 Blur，
    /// 仍不支持则退回不透明窗口，因此选 Mica 是安全的。
    /// </summary>
    public string WindowBlur { get; set; } = "Off";

    /// <summary>工具调用循环的最大轮数，防止无限循环。</summary>
    public int MaxToolRounds { get; set; } = 5;

    /// <summary>
    /// 危险工具是否逐次确认。
    ///
    /// 默认开启：智能体的「允许执行危险工具」只是说"这个智能体有资格"，
    /// 不等于每一次调用都该放行——模型可能被工具返回值里的提示注入带偏。
    /// 关掉则退回一次授权全程放行的行为。
    /// </summary>
    public bool ConfirmDangerousTools { get; set; } = true;

    /// <summary>
    /// 思考模式（扩展推理）。开启后让支持的模型先推理再回答，并在消息里展示推理过程。
    /// 具体模型是否返回推理内容由运行时决定：OpenAI o 系列通过 <c>reasoning_effort</c> 请求，
    /// DeepSeek-R1 / Qwen-QwQ 等会自动返回，其余模型不发送该参数也不会报错。
    /// </summary>
    public bool ThinkingEnabled { get; set; }

    /// <summary>
    /// 单次生成的超时时间（秒）。这是整个流式请求的总时长上限，
    /// 不是空闲检测——因此默认给得比较宽，避免长回答被中途掐断。
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 300;
}
