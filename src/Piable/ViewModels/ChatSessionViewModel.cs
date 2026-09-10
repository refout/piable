using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Piable.Models;
using Piable.Services;

namespace Piable.ViewModels;

/// <summary>
/// 单个会话的交互逻辑：消息列表、流式生成、中断控制、统计汇总。
/// </summary>
public sealed partial class ChatSessionViewModel : ViewModelBase
{
    private readonly ISessionService _sessions;
    private readonly IAgentOrchestrator _orchestrator;
    private readonly ITokenCostCalculator _calculator;
    private readonly UserPreferences _preferences;
    private readonly IStatusReporter _status;

    private CancellationTokenSource? _generationCts;

    /// <summary>区分"用户点了停止"与"请求超时"——两者都表现为取消异常。</summary>
    private bool _userCancelled;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _inputText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isGenerating;

    [ObservableProperty]
    private bool _isParameterPanelExpanded;

    [ObservableProperty]
    private Agent? _selectedAgent;

    [ObservableProperty]
    private double _temperature = 0.7;

    [ObservableProperty]
    private int _maxTokens = 2048;

    [ObservableProperty]
    private double _topP = 1.0;

    public ChatSessionViewModel(
        ChatSession session,
        IReadOnlyList<Agent> availableAgents,
        ProviderConfig? provider,
        ISessionService sessions,
        IAgentOrchestrator orchestrator,
        ITokenCostCalculator calculator,
        UserPreferences preferences,
        IStatusReporter status)
    {
        Session = session;
        AvailableAgents = availableAgents;
        Provider = provider;
        _sessions = sessions;
        _orchestrator = orchestrator;
        _calculator = calculator;
        _preferences = preferences;
        _status = status;

        _selectedAgent = availableAgents.FirstOrDefault(a => a.Id == session.AgentId)
                         ?? availableAgents.FirstOrDefault();

        ApplyParameterDefaults();

        foreach (var message in session.Messages)
        {
            Messages.Add(new MessageViewModel(message, _calculator, _preferences));
        }
    }

    public ChatSession Session { get; }

    public ObservableCollection<MessageViewModel> Messages { get; } = [];

    public IReadOnlyList<Agent> AvailableAgents { get; }

    /// <summary>当前会话使用的供应商；由主窗口在配置变化时更新。</summary>
    [ObservableProperty]
    private ProviderConfig? _provider;

    public string Title => Session.Title;

    /// <summary>标题栏统计：<c>💬 12条 · 🔢 2.3K · ⏱ 8.7s · 🤖 通用助手</c>。</summary>
    public string HeaderStatistics
    {
        get
        {
            var tokens = Messages.Sum(m => (long)(m.TotalTokens ?? 0));
            var duration = Messages.Sum(m => m.DurationMs ?? 0);
            var agent = SelectedAgent?.Name ?? "未选择智能体";

            return $"💬 {Messages.Count}条 · 🔢 {_calculator.FormatTokens(tokens)}"
                   + $" · ⏱ {_calculator.FormatDuration(duration)} · 🤖 {agent}";
        }
    }

    /// <summary>当前是否处于可见状态，用于抑制后台会话的滚动请求。</summary>
    public bool IsActive { get; set; }

    /// <summary>生成过程中文本有更新，请求界面滚动到底部。</summary>
    public event EventHandler? ScrollToEndRequested;

    /// <summary>会话标题变化，通知侧边栏刷新。</summary>
    public event EventHandler? TitleChanged;

    /// <summary>一轮生成结束（含失败与中断），侧边栏据此刷新条数与 token。</summary>
    public event EventHandler? TurnCompleted;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = InputText.Trim();
        if (text.Length == 0 || IsGenerating)
        {
            return;
        }

        if (Provider is null)
        {
            _status.ReportError("尚未配置供应商，请先在「配置 → API 配置」中设置。");
            return;
        }

        var agent = SelectedAgent ?? new Agent { SystemPrompt = string.Empty };
        var model = ResolveModel(agent);

        if (string.IsNullOrWhiteSpace(model))
        {
            _status.ReportError("尚未选择模型，请先在「配置 → API 配置」中设置默认模型。");
            return;
        }

        InputText = string.Empty;
        IsGenerating = true;
        _userCancelled = false;
        _status.SetBusy("生成中…");

        try
        {
            await RunTurnAsync(text, agent, model).ConfigureAwait(true);
        }
        finally
        {
            IsGenerating = false;
            _generationCts?.Dispose();
            _generationCts = null;
            _status.SetBusy(null);
            NotifyHeaderChanged();
        }
    }

    private async Task RunTurnAsync(string text, Agent agent, string model)
    {
        var wasFirstMessage = Session.Messages.Count == 0;

        // ---- 用户消息：立即落库，避免异常时丢失用户输入 ----
        var userMessage = new ChatMessage { Role = MessageRole.User, Content = text };
        Session.Messages.Add(userMessage);
        Messages.Add(new MessageViewModel(userMessage, _calculator, _preferences));
        await _sessions.AppendMessageAsync(Session.Id, userMessage).ConfigureAwait(true);

        if (wasFirstMessage)
        {
            await _sessions.RenameFromFirstMessageAsync(Session, text).ConfigureAwait(true);
            TitleChanged?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged(nameof(Title));
        }

        // 历史在加入助手占位消息之前取，否则请求里会多出一条空助手消息
        var history = Session.Messages.ToList();

        // ---- 助手占位消息 ----
        var assistantMessage = new ChatMessage
        {
            Role = MessageRole.Assistant,
            Content = string.Empty,
            StartTime = DateTimeOffset.Now,
            ModelUsed = model,
        };
        var assistantView = new MessageViewModel(assistantMessage, _calculator, _preferences);
        Messages.Add(assistantView);
        ScrollToEndRequested?.Invoke(this, EventArgs.Empty);

        // ---- 流式生成 ----
        var request = new ChatRequest
        {
            Provider = Provider!,
            Agent = agent,
            History = history,
            Model = model,
            Temperature = Temperature,
            MaxTokens = MaxTokens,
            TopP = TopP,
        };

        _generationCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(Math.Max(1, _preferences.RequestTimeoutSeconds)));

        ChatUsage? usage = null;
        var stopwatch = Stopwatch.StartNew();
        var interrupted = false;

        try
        {
            await foreach (var chunk in _orchestrator
                .StreamAsync(request, _generationCts.Token)
                .ConfigureAwait(true))
            {
                if (chunk.TextDelta is not null)
                {
                    assistantView.AppendText(chunk.TextDelta);
                    if (IsActive)
                    {
                        ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
                    }
                }

                if (chunk.Usage is not null)
                {
                    usage = chunk.Usage;
                }
            }
        }
        catch (OperationCanceledException) when (_userCancelled)
        {
            interrupted = true;
        }
        catch (OperationCanceledException)
        {
            // 不是用户取消，那就是 CancelAfter 触发的超时
            interrupted = true;
            _status.ReportError("⏰ 请求超时，请检查网络");
        }
        catch (Exception ex)
        {
            interrupted = true;
            var message = ChatErrorMapper.ToUserMessage(ex);
            if (message is not null)
            {
                _status.ReportError(message);
            }
        }
        finally
        {
            stopwatch.Stop();
        }

        var hasContent = assistantMessage.Content.Length > 0;

        // 请求彻底失败且没吐出任何内容：撤掉这个空气泡，不要让历史里留下空消息
        if (interrupted && !hasContent)
        {
            Messages.Remove(assistantView);
            return;
        }

        var cost = _calculator.CalculateCost(
            usage?.PromptTokens,
            usage?.CompletionTokens,
            Provider!.InputPricePer1K,
            Provider.OutputPricePer1K);

        assistantView.ApplyStatistics(stopwatch.Elapsed, usage, cost, model, interrupted);

        if (interrupted)
        {
            // 设计文档 4.2：在末尾标记已中断。
            // AppendText 直接改的就是 assistantMessage.Content（同一个对象）。
            assistantView.AppendText("\n\n*[已中断]*");
        }

        Session.ModelUsed = model;
        Session.AgentSnapshot = SessionService.CreateAgentSnapshot(agent);
        Session.Messages.Add(assistantMessage);

        // 后台会话的 UpdatedAt 由落库时刷新；这里同步内存态，供侧边栏排序
        Session.UpdatedAt = DateTimeOffset.Now;

        await _sessions.AppendMessageAsync(Session.Id, assistantMessage).ConfigureAwait(true);
        await _sessions.SaveMetadataAsync(Session, CancellationToken.None).ConfigureAwait(true);

        ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
        TurnCompleted?.Invoke(this, EventArgs.Empty);
    }

    private string ResolveModel(Agent agent)
    {
        if (!string.IsNullOrWhiteSpace(agent.Model))
        {
            return agent.Model;
        }

        if (!string.IsNullOrWhiteSpace(Provider?.DefaultModel))
        {
            return Provider.DefaultModel;
        }

        return Provider?.DeploymentName ?? string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        _userCancelled = true;
        _generationCts?.Cancel();
    }

    private bool CanSend() => !IsGenerating && !string.IsNullOrWhiteSpace(InputText);

    private bool CanStop() => IsGenerating;

    partial void OnSelectedAgentChanged(Agent? value)
    {
        if (value is not null)
        {
            Session.AgentId = value.Id;
            Session.AgentSnapshot = SessionService.CreateAgentSnapshot(value);
        }

        ApplyParameterDefaults();
        NotifyHeaderChanged();
    }

    /// <summary>参数取值优先级：智能体覆盖 → 供应商覆盖 → 内置默认。</summary>
    private void ApplyParameterDefaults()
    {
        Temperature = SelectedAgent?.Temperature ?? Provider?.Temperature ?? 0.7;
        MaxTokens = SelectedAgent?.MaxTokens ?? Provider?.MaxTokens ?? 2048;
        TopP = SelectedAgent?.TopP ?? Provider?.TopP ?? 1.0;
    }

    /// <summary>偏好变化后刷新统计显示。</summary>
    public void RefreshStatistics()
    {
        foreach (var message in Messages)
        {
            message.RefreshStatistics();
        }

        NotifyHeaderChanged();
    }

    /// <summary>重新计算标题栏统计。</summary>
    public void NotifyHeaderChanged() => OnPropertyChanged(nameof(HeaderStatistics));

    /// <summary>用户改了标题。</summary>
    public void NotifyTitleChanged()
    {
        OnPropertyChanged(nameof(Title));
        TitleChanged?.Invoke(this, EventArgs.Empty);
    }
}
