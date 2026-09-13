using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Piable.Helpers;
using Piable.Services;

namespace Piable.ViewModels;

/// <summary>
/// 对话流中的一张危险工具确认卡片。
///
/// 它同时是界面条目与等待句柄：编排器在执行前挂起，等用户在卡片上点了"允许"或"拒绝"
/// 才继续。放在对话流里而不是弹模态框，是因为生成是流式的——弹框会把上下文盖住，
/// 而卡片留在原地，之后回看时能清楚看到"当时批准了什么"。
/// </summary>
public sealed partial class ToolConfirmationViewModel : ChatItemViewModel
{
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ToolConfirmationViewModel(ToolConfirmationRequest request)
    {
        ToolName = request.ToolName;
        DisplayName = request.DisplayName;
        SourceLabel = request.SourceLabel;
        ArgumentsText = request.ArgumentsText;
        Description = request.Description;
    }

    public string ToolName { get; }

    public string DisplayName { get; }

    public string SourceLabel { get; }

    public string ArgumentsText { get; }

    public string? Description { get; }

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public override bool IsUser => false;

    /// <summary>等待用户决定的任务。允许为 true，拒绝为 false。</summary>
    public Task<bool> Completion => _completion.Task;

    /// <summary>"本次会话内不再询问"勾选后，后续同类调用自动放行。</summary>
    [ObservableProperty]
    private bool _trustForThisSession;

    [ObservableProperty]
    private bool _isResolved;

    /// <summary>
    /// 卡片标题：<c>是否允许执行 执行命令（run_shell）</c>。
    /// 展示名与工具名不一致时两个都给——模型说的是工具名，用户认的是展示名，
    /// 只给其中一个都会有人对不上号。
    /// </summary>
    public string Headline =>
        string.Equals(DisplayName, ToolName, StringComparison.Ordinal)
            ? Loc.Get("Tool.ConfirmHeadline", DisplayName)
            : Loc.Get("Tool.ConfirmHeadlineBoth", DisplayName, ToolName);

    /// <summary>结果文案。未决定时为空，界面据此隐藏结果行。</summary>
    [ObservableProperty]
    private string _resolutionText = string.Empty;

    [RelayCommand]
    private void Allow()
    {
        IsResolved = true;
        ResolutionText = TrustForThisSession ? Loc.Get("Tool.AllowedForSession") : Loc.Get("Tool.Allowed");
        _completion.TrySetResult(true);
    }

    [RelayCommand]
    private void Reject()
    {
        IsResolved = true;
        ResolutionText = Loc.Get("Tool.Rejected");
        _completion.TrySetResult(false);
    }

    /// <summary>
    /// 会话被切换或生成被取消时调用，避免等待方永远挂着。
    /// 与"拒绝"区分开：这不是用户的选择，所以不写结果文案。
    /// </summary>
    public void Abandon() => _completion.TrySetResult(false);
}
