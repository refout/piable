using CommunityToolkit.Mvvm.ComponentModel;
using Piable.Models;

namespace Piable.ViewModels;

/// <summary>侧边栏中的一条会话。</summary>
public sealed partial class SessionListItemViewModel : InlineConfirmViewModel
{
    [ObservableProperty]
    private bool _isSelected;

    public SessionListItemViewModel(ChatSessionSummary summary, string agentName)
    {
        Id = summary.Id;
        Title = summary.Title;
        MessageCount = summary.MessageCount;
        TotalTokens = summary.TotalTokens;
        AgentName = agentName;
        UpdatedAt = summary.UpdatedAt;
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

    public DateTimeOffset UpdatedAt { get; }

    /// <summary>列表项第二行：<c>💬 12条 · 🔢 2.3K tokens · 🤖 通用助手</c>。</summary>
    public string SummaryText
    {
        get
        {
            var tokens = TotalTokens < 1000
                ? TotalTokens.ToString()
                : (TotalTokens / 1000d).ToString("0.#") + "K";

            return $"💬 {MessageCount}条 · 🔢 {tokens} tokens · 🤖 {AgentName}";
        }
    }

    /// <summary>从摘要刷新（生成结束后侧边栏需要更新条数与 token）。</summary>
    public void Update(ChatSessionSummary summary, string agentName)
    {
        Title = summary.Title;
        MessageCount = summary.MessageCount;
        TotalTokens = summary.TotalTokens;
        AgentName = agentName;
        OnPropertyChanged(nameof(SummaryText));
    }

    /// <summary>重新求值第二行文本（标题或统计在别处被改动后调用）。</summary>
    public void RefreshSummary() => OnPropertyChanged(nameof(SummaryText));
}
