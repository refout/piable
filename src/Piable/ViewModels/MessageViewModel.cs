using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveMarkdown.Avalonia;
using Piable.Helpers;
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
public sealed partial class MessageViewModel : ChatItemViewModel
{
    private readonly ITokenCostCalculator _calculator;
    private readonly UserPreferences _preferences;

    /// <summary>统计明细是否展开（点击 token 文字切换）。</summary>
    [ObservableProperty]
    private bool _isStatisticsExpanded;

    /// <summary>思考块是否展开（默认展开，便于直接看到推理过程）。</summary>
    [ObservableProperty]
    private bool _isThinkingExpanded = true;

    /// <summary>推理是否仍在进行中（流式生成期间为 true，落库/回答开始后为 false）。
    /// 驱动思考条显示"思考中"还是"已思考"。</summary>
    [ObservableProperty]
    private bool _isThinking;

    partial void OnIsThinkingChanged(bool value) =>
        OnPropertyChanged(nameof(ThinkingStateLabel));

    public MessageViewModel(ChatMessage model, ITokenCostCalculator calculator, UserPreferences preferences)
    {
        Model = model;
        _calculator = calculator;
        _preferences = preferences;
        _isStatisticsExpanded = preferences.ExpandStatisticsByDefault;

        // LiveMarkdown 的流式渲染直接订阅这个可观察字符串，
        // 逐段 Append 即可增量重排，无需每次重新解析整篇内容。
        MarkdownBuilder = new ObservableStringBuilder(model.Content);
        ThinkingBuilder = new ObservableStringBuilder(model.ThinkingContent ?? string.Empty);
    }

    public ChatMessage Model { get; }

    /// <summary>供 MarkdownRenderer.MarkdownBuilder 绑定的流式 Markdown 源。</summary>
    public ObservableStringBuilder MarkdownBuilder { get; }

    /// <summary>供思考块 MarkdownRenderer 绑定的流式推理源。</summary>
    public ObservableStringBuilder ThinkingBuilder { get; }

    public override bool IsUser => Model.Role == MessageRole.User;

    public bool IsAssistant => Model.Role == MessageRole.Assistant;

    /// <summary>消息正文。仅在流式生成过程中通过 <see cref="AppendText"/> 变化。</summary>
    public string Content => Model.Content;

    /// <summary>推理/思考内容（可能为 null）。</summary>
    public string? ThinkingContent => Model.ThinkingContent;

    /// <summary>是否有可展示的推理内容，用于控制思考块的显隐。</summary>
    public bool HasThinking => !string.IsNullOrEmpty(Model.ThinkingContent);

    /// <summary>思考条的状态文案：推理进行中显示"思考中"，结束后显示"已思考"。</summary>
    public string ThinkingStateLabel =>
        IsThinking ? Loc.Get("Chat.ThinkingInProgress") : Loc.Get("Chat.ThinkingDone");

    public bool IsInterrupted => Model.IsInterrupted;

    public long? DurationMs => Model.DurationMs;

    public int? PromptTokens => Model.PromptTokens;

    public int? CompletionTokens => Model.CompletionTokens;

    public int? TotalTokens => Model.TotalTokens;

    public decimal? EstimatedCost => Model.EstimatedCost;

    /// <summary>只有助手消息且确有统计信息时才显示统计条。</summary>
    public bool HasStatistics =>
        IsAssistant && (Model.DurationMs is not null || Model.TotalTokens is not null);

    /// <summary>是否显示费用。费用未知时为 false，避免显示一个无意义的钱图标。</summary>
    public bool HasCost => _preferences.ShowCost && Model.EstimatedCost is not null;

    /// <summary>默认态：<c>2.3s │ 201 tokens</c>。</summary>
    public string StatisticsSummary =>
        $"{_calculator.FormatDuration(Model.DurationMs)}   │   {_calculator.FormatTokens(Model.TotalTokens)} tokens";

    /// <summary>展开态：<c>2.3s │ 45 │ 156 │ $0.0003</c>。</summary>
    public string StatisticsDetails
    {
        get
        {
            var text = $"{_calculator.FormatDuration(Model.DurationMs)}   │   "
                       + $"{FormatCount(Model.PromptTokens)}   │   "
                       + $"{FormatCount(Model.CompletionTokens)}";

            if (HasCost)
            {
                text += $"   │   {_calculator.FormatCost(Model.EstimatedCost, _preferences.Currency)}";
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
        MarkdownBuilder.Append(delta);

        // 回答开始即代表推理阶段结束
        if (IsThinking)
        {
            IsThinking = false;
        }

        OnPropertyChanged(nameof(Content));
    }

    /// <summary>流式生成过程中追加推理/思考内容。</summary>
    public void AppendThinking(string delta)
    {
        Model.ThinkingContent = (Model.ThinkingContent ?? string.Empty) + delta;
        ThinkingBuilder.Append(delta);

        // 首段推理到达即进入"思考中"状态
        if (!IsThinking)
        {
            IsThinking = true;
        }

        OnPropertyChanged(nameof(HasThinking));
        OnPropertyChanged(nameof(ThinkingContent));
    }

    /// <summary>标记推理结束（落库或最终回答开始时调用），让思考条切到"已思考"。</summary>
    public void EndThinking()
    {
        if (IsThinking)
        {
            IsThinking = false;
        }
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

    /// <summary>点击统计文字：在摘要与明细之间切换（设计文档 9.3）。</summary>
    [RelayCommand]
    private void ToggleStatistics() => IsStatisticsExpanded = !IsStatisticsExpanded;

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
