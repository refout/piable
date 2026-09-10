using CommunityToolkit.Mvvm.ComponentModel;
using Piable.Models;
using Piable.Services;

namespace Piable.ViewModels;

/// <summary>
/// 单条消息的展示模型。
///
/// 统计文本不缓存，而是每次按当前的用户偏好实时算出：偏好对象是共享且可变的，
/// 用户切换"显示费用"后只需通知各消息重新求值即可。这样避免了
/// 消息订阅偏好变更事件所带来的生命周期纠缠（消息会一直引用着偏好对象）。
/// </summary>
public sealed partial class MessageViewModel : ViewModelBase
{
    private readonly ITokenCostCalculator _calculator;
    private readonly UserPreferences _preferences;

    /// <summary>统计明细是否展开（点击 token 文字切换）。</summary>
    [ObservableProperty]
    private bool _isStatisticsExpanded;

    public MessageViewModel(ChatMessage model, ITokenCostCalculator calculator, UserPreferences preferences)
    {
        Model = model;
        _calculator = calculator;
        _preferences = preferences;
        _isStatisticsExpanded = preferences.ExpandStatisticsByDefault;
    }

    public ChatMessage Model { get; }

    public MessageRole Role => Model.Role;

    public bool IsUser => Model.Role == MessageRole.User;

    public bool IsAssistant => Model.Role == MessageRole.Assistant;

    /// <summary>消息正文。仅在流式生成过程中通过 <see cref="AppendText"/> 变化。</summary>
    public string Content => Model.Content;

    public bool IsInterrupted => Model.IsInterrupted;

    public long? DurationMs => Model.DurationMs;

    public int? PromptTokens => Model.PromptTokens;

    public int? CompletionTokens => Model.CompletionTokens;

    public int? TotalTokens => Model.TotalTokens;

    public decimal? EstimatedCost => Model.EstimatedCost;

    public string TimestampText => Model.Timestamp.ToLocalTime().ToString("HH:mm");

    /// <summary>只有助手消息且确有统计信息时才显示统计条。</summary>
    public bool HasStatistics =>
        IsAssistant && (Model.DurationMs is not null || Model.TotalTokens is not null);

    /// <summary>是否显示费用。费用未知时为 false，避免显示一个无意义的钱图标。</summary>
    public bool HasCost => _preferences.ShowCost && Model.EstimatedCost is not null;

    /// <summary>默认态：<c>⏱ 2.3s │ 🔢 201 tokens</c>。</summary>
    public string StatisticsSummary =>
        $"⏱ {_calculator.FormatDuration(Model.DurationMs)}   │   🔢 {_calculator.FormatTokens(Model.TotalTokens)} tokens";

    /// <summary>展开态：<c>⏱ 2.3s │ 📥 45 │ 📤 156 │ 💰 $0.0003</c>。</summary>
    public string StatisticsDetails
    {
        get
        {
            var text = $"⏱ {_calculator.FormatDuration(Model.DurationMs)}   │   "
                       + $"📥 {FormatCount(Model.PromptTokens)}   │   "
                       + $"📤 {FormatCount(Model.CompletionTokens)}";

            if (HasCost)
            {
                text += $"   │   💰 {_calculator.FormatCost(Model.EstimatedCost, _preferences.Currency)}";
            }

            return text;
        }
    }

    /// <summary>当前应展示的统计文本。</summary>
    public string StatisticsText =>
        ShowDetailedStatistics ? StatisticsDetails : StatisticsSummary;

    /// <summary>明细展开的条件：手动展开，或偏好里要求默认展开详细 Token。</summary>
    public bool ShowDetailedStatistics =>
        IsStatisticsExpanded || _preferences.ShowDetailedTokens;

    /// <summary>整体是否显示统计（偏好关闭时整条隐藏）。</summary>
    public bool ShouldShowStatistics => _preferences.ShowStatistics && HasStatistics;

    /// <summary>流式生成过程中追加文本。</summary>
    public void AppendText(string delta)
    {
        Model.Content += delta;
        OnPropertyChanged(nameof(Content));
    }

    /// <summary>生成结束后一次性写入统计信息。</summary>
    public void ApplyStatistics(
        TimeSpan duration,
        ChatUsage? usage,
        decimal? cost,
        string? modelUsed,
        bool interrupted)
    {
        Model.DurationMs = (long)duration.TotalMilliseconds;
        Model.EndTime = DateTimeOffset.Now;
        Model.ModelUsed = modelUsed;
        Model.IsInterrupted = interrupted;

        if (usage is not null)
        {
            Model.PromptTokens = usage.PromptTokens;
            Model.CompletionTokens = usage.CompletionTokens;
            Model.TotalTokens = usage.TotalTokens;
        }
        else if (!interrupted)
        {
            // 供应商没汇报 usage 时按内容长度兜底估算（设计文档 7.5）。
            // 被中断的回答不估算——残缺文本推不出有意义的用量。
            var completion = TokenEstimator.Estimate(Model.Content);
            Model.CompletionTokens = completion;
            Model.TotalTokens = completion;
        }

        Model.EstimatedCost = cost;

        OnPropertyChanged(nameof(DurationMs));
        OnPropertyChanged(nameof(PromptTokens));
        OnPropertyChanged(nameof(CompletionTokens));
        OnPropertyChanged(nameof(TotalTokens));
        OnPropertyChanged(nameof(EstimatedCost));
        OnPropertyChanged(nameof(IsInterrupted));
        OnPropertyChanged(nameof(HasStatistics));
        OnPropertyChanged(nameof(HasCost));
        OnPropertyChanged(nameof(StatisticsText));
        OnPropertyChanged(nameof(ShouldShowStatistics));
    }

    /// <summary>用户偏好变化后重新求值统计文本。</summary>
    public void RefreshStatistics()
    {
        OnPropertyChanged(nameof(StatisticsSummary));
        OnPropertyChanged(nameof(StatisticsDetails));
        OnPropertyChanged(nameof(StatisticsText));
        OnPropertyChanged(nameof(ShowDetailedStatistics));
        OnPropertyChanged(nameof(HasCost));
        OnPropertyChanged(nameof(ShouldShowStatistics));
    }

    private string FormatCount(int? value) =>
        value is null ? "--" : _calculator.FormatTokens(value);
}
