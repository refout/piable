using CommunityToolkit.Mvvm.ComponentModel;
using Piable.Helpers;
using Piable.Models;

namespace Piable.ViewModels;

/// <summary>侧边栏中的一条会话。</summary>
public sealed partial class SessionListItemViewModel : InlineConfirmViewModel
{
    public SessionListItemViewModel(ChatSessionSummary summary, string agentName)
    {
        Id = summary.Id;
        Title = summary.Title;
        MessageCount = summary.MessageCount;
        TotalTokens = summary.TotalTokens;
        AgentName = agentName;
        UpdatedAt = summary.UpdatedAt;
        MatchCount = summary.MatchCount;
    }

    public string Id { get; }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private int _messageCount;

    [ObservableProperty]
    private long _totalTokens;

    [ObservableProperty]
    private string _agentName;

    /// <summary>会话更新时间。搜索结束后恢复列表时用它排序，避免打乱原有的时间顺序。</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>搜索命中的消息条数。0 表示这次不是搜索结果（或只命中标题）。</summary>
    [ObservableProperty]
    private int _matchCount;

    /// <summary>是否为搜索结果。</summary>
    public bool IsSearchHit => IsSearching && MatchCount > 0;

    /// <summary>当前是否处于搜索态。由主窗口设置，同一时刻对所有行一致。</summary>
    public bool IsSearching { get; set; }

    /// <summary>列表项第二行：<c>12条 · 2.3K tokens · 通用助手</c>。</summary>
    public string SummaryText
    {
        get
        {
            var tokens = TotalTokens < 1000
                ? TotalTokens.ToString()
                : (TotalTokens / 1000d).ToString("0.#") + "K";

            // 搜索态下把"命中几条"放在最前：这是用户判断要不要点开的唯一依据，
            // 而消息总数与 token 数在找东西时毫无参考价值。
            var head = IsSearchHit
                ? Loc.Get("Session.HitCount", MatchCount)
                : Loc.Get("Session.MessageCount", MessageCount);

            return $"{head}{tokens} tokens · {AgentName}";
        }
    }

    /// <summary>从摘要刷新（生成结束后侧边栏需要更新条数与 token）。</summary>
    public void Update(ChatSessionSummary summary, string agentName)
    {
        Title = summary.Title;
        MessageCount = summary.MessageCount;
        TotalTokens = summary.TotalTokens;
        AgentName = agentName;
        UpdatedAt = summary.UpdatedAt;
        OnPropertyChanged(nameof(SummaryText));
    }

    /// <summary>重新求值第二行文本（标题或统计在别处被改动后调用）。</summary>
    public void RefreshSummary() => OnPropertyChanged(nameof(SummaryText));
}
