using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Piable.Models;

namespace Piable.ViewModels;

/// <summary>
/// 对话流中的一次工具调用。
/// 工具调用始终完整展示（名称、参数、结果），而不是折叠成一行摘要——
/// 用户需要能看清模型到底请求了什么、实际返回了什么，这是判断回答可信度的前提。
/// </summary>
public sealed partial class ToolCallViewModel : ChatItemViewModel
{
    /// <summary>结果过长时折叠展示，避免一次命令的输出把整屏占满。</summary>
    private const int PreviewLength = 400;

    [ObservableProperty]
    private bool _isExpanded;

    public ToolCallViewModel(ToolCallPayload payload, UserPreferences preferences)
    {
        Payload = payload;
        _isExpanded = preferences.ExpandStatisticsByDefault;
    }

    public ToolCallPayload Payload { get; }

    public override bool IsUser => false;

    public string DisplayName => Payload.DisplayName;

    public string SourceLabel => Payload.SourceLabel;

    public string ArgumentsText => Payload.ArgumentsText;

    public ToolRisk Risk => Payload.Risk;

    public ToolInvocationStatusPayload Status => Payload.Status;

    public bool IsDangerous => Payload.Risk == ToolRisk.Dangerous;

    public bool IsDenied => Payload.Status == ToolInvocationStatusPayload.Denied;

    public bool IsFailed => Payload.Status == ToolInvocationStatusPayload.Failed;

    /// <summary>状态图标。被拒绝用禁止符，与执行失败区分开。</summary>
    public string StatusIcon => Payload.Status switch
    {
        ToolInvocationStatusPayload.Succeeded => "🔧",
        ToolInvocationStatusPayload.Denied => "⛔",
        _ => "⚠️",
    };

    /// <summary>一行摘要：<c>🔧 run_shell(command=ls -la)</c>。</summary>
    public string Summary => $"{StatusIcon} {Payload.DisplayName}（{ArgumentsText}）";

    public string DurationText => Payload.DurationMs <= 0
        ? string.Empty
        : $"{(Payload.DurationMs / 1000d):0.0}s";

    public bool HasDuration => Payload.DurationMs > 0;

    /// <summary>结果文本。过长时未展开只显示前若干字符。</summary>
    public string ResultText
    {
        get
        {
            var text = Payload.ResultPayload;

            if (IsExpanded || text.Length <= PreviewLength)
            {
                return text;
            }

            return text[..PreviewLength] + "…";
        }
    }

    public bool IsTruncatable => Payload.ResultPayload.Length > PreviewLength;

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ResultText));

    /// <summary>由编排器的调用记录构造持久化载荷。</summary>
    public static ToolCallPayload ToPayload(Services.ToolInvocationRecord record) => new()
    {
        ToolName = record.ToolName,
        DisplayName = record.DisplayName,
        SourceLabel = record.SourceLabel,
        Risk = record.Risk,
        Status = record.Status switch
        {
            Services.ToolInvocationStatus.Denied => ToolInvocationStatusPayload.Denied,
            Services.ToolInvocationStatus.Failed => ToolInvocationStatusPayload.Failed,
            _ => ToolInvocationStatusPayload.Succeeded,
        },
        ArgumentsText = record.ArgumentsText,
        ResultPayload = record.ResultPayload,
        DurationMs = (long)record.Duration.TotalMilliseconds,
    };
}
