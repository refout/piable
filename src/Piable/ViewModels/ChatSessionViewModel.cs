using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Piable.Models;
using Piable.Services;
using Piable.Services.Tools;

namespace Piable.ViewModels;

/// <summary>
/// 单个会话的交互逻辑：消息与工具调用列表、流式生成、中断控制、统计汇总。
/// </summary>
public sealed partial class ChatSessionViewModel : ViewModelBase
{
    private readonly ISessionService _sessions;
    private readonly IAgentOrchestrator _orchestrator;
    private readonly IToolCatalog _toolCatalog;
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
        IToolCatalog toolCatalog,
        ITokenCostCalculator calculator,
        UserPreferences preferences,
        IStatusReporter status)
    {
        Session = session;
        AvailableAgents = availableAgents;
        Provider = provider;
        _sessions = sessions;
        _orchestrator = orchestrator;
        _toolCatalog = toolCatalog;
        _calculator = calculator;
        _preferences = preferences;
        _status = status;

        _selectedAgent = availableAgents.FirstOrDefault(a => a.Id == session.AgentId)
                         ?? availableAgents.FirstOrDefault();

        ApplyParameterDefaults();

        foreach (var message in session.Messages)
        {
            Items.Add(CreateItem(message));
        }
    }

    public ChatSession Session { get; }

    /// <summary>对话流。消息与工具调用按发生顺序混排。</summary>
    public ObservableCollection<ChatItemViewModel> Items { get; } = [];

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
            var messages = Items.OfType<MessageViewModel>().ToList();
            var tokens = messages.Sum(m => (long)(m.TotalTokens ?? 0));
            var duration = messages.Sum(m => m.DurationMs ?? 0);
            var agent = SelectedAgent?.Name ?? "未选择智能体";

            return $"💬 {messages.Count}条 · 🔢 {_calculator.FormatTokens(tokens)}"
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
        var isFirstMessage = Session.Messages.Count == 0;

        // ---- 用户消息：立即落库，避免后续异常时丢失用户输入 ----
        var userMessage = new ChatMessage { Role = MessageRole.User, Content = text };
        Session.Messages.Add(userMessage);
        Items.Add(new MessageViewModel(userMessage, _calculator, _preferences));
        await _sessions.AppendMessageAsync(Session.Id, userMessage).ConfigureAwait(true);

        if (isFirstMessage)
        {
            await _sessions.RenameFromFirstMessageAsync(Session, text).ConfigureAwait(true);
            TitleChanged?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged(nameof(Title));
        }

        // 历史在加入本轮新消息之前取，否则请求里会混进还没发生的助手回复
        var history = Session.Messages.ToList();

        _generationCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(Math.Max(1, _preferences.RequestTimeoutSeconds)));

        var tools = await ResolveToolsAsync(agent).ConfigureAwait(true);

        var request = new ChatRequest
        {
            Provider = Provider!,
            Agent = agent,
            History = history,
            Model = model,
            Temperature = Temperature,
            MaxTokens = MaxTokens,
            TopP = TopP,
            Tools = tools,
            AllowDangerousTools = agent.AllowDangerousTools,
            MaxToolRounds = _preferences.MaxToolRounds,
        };

        var outcome = await ConsumeStreamAsync(request, model).ConfigureAwait(true);

        var cost = _calculator.CalculateCost(
            outcome.Usage?.PromptTokens,
            outcome.Usage?.CompletionTokens,
            Provider!.InputPricePer1K,
            Provider.OutputPricePer1K);

        ApplyTurnStatistics(outcome, cost, model);

        Session.ModelUsed = model;
        Session.AgentSnapshot = SessionService.CreateAgentSnapshot(agent);
        Session.UpdatedAt = DateTimeOffset.Now;
        await _sessions.SaveMetadataAsync(Session, CancellationToken.None).ConfigureAwait(true);

        ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
        TurnCompleted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>本轮生成的结果汇总。</summary>
    private sealed record TurnOutcome(ChatUsage? Usage, TimeSpan Duration, bool Interrupted);

    /// <summary>
    /// 消费流式结果，边收边渲染，并在每条消息"封口"时立刻落库。
    /// 逐条落库而非攒到最后统一写，是为了让崩溃或强制退出最多只损失正在生成的那一条。
    /// </summary>
    private async Task<TurnOutcome> ConsumeStreamAsync(ChatRequest request, string model)
    {
        ChatUsage? usage = null;

        // 当前正在增长的助手消息。遇到工具调用就"封口"，工具之后的文本会开启
        // 一条新的助手消息——这样重新加载历史时顺序依然正确。
        MessageViewModel? current = null;
        ChatMessage? currentModel = null;

        var stopwatch = Stopwatch.StartNew();
        var interrupted = false;

        try
        {
            await foreach (var chunk in _orchestrator
                .StreamAsync(request, _generationCts!.Token)
                .ConfigureAwait(true))
            {
                if (chunk.TextDelta is not null)
                {
                    if (current is null)
                    {
                        currentModel = new ChatMessage
                        {
                            Role = MessageRole.Assistant,
                            Content = string.Empty,
                            StartTime = DateTimeOffset.Now,
                            ModelUsed = model,
                        };
                        current = new MessageViewModel(currentModel, _calculator, _preferences);
                        Items.Add(current);
                    }

                    current.AppendText(chunk.TextDelta);

                    if (IsActive)
                    {
                        ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
                    }
                }

                if (chunk.Usage is not null)
                {
                    usage = chunk.Usage;
                }

                if (chunk.ToolCall is not null)
                {
                    await CloseAssistantAsync(currentModel, current).ConfigureAwait(true);
                    current = null;
                    currentModel = null;

                    await AppendToolCallAsync(chunk.ToolCall).ConfigureAwait(true);
                    ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
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

        await CloseAssistantAsync(currentModel, current, interrupted).ConfigureAwait(true);

        return new TurnOutcome(usage, stopwatch.Elapsed, interrupted);
    }

    /// <summary>
    /// 收尾一条助手消息：按需补上中断标记，写入历史并落库。
    /// 内容为空的（模型直接请求工具、或请求彻底失败）不落库，避免历史里留下空消息。
    /// </summary>
    private async Task CloseAssistantAsync(
        ChatMessage? model, MessageViewModel? view, bool interrupted = false)
    {
        if (model is null || view is null)
        {
            return;
        }

        if (model.Content.Length == 0)
        {
            Items.Remove(view);
            return;
        }

        if (interrupted)
        {
            // 设计文档 4.2：在末尾标记已中断
            view.AppendText("\n\n*[已中断]*");
        }

        Session.Messages.Add(model);
        await _sessions.AppendMessageAsync(Session.Id, model).ConfigureAwait(true);
    }

    private async Task AppendToolCallAsync(ToolInvocationRecord record)
    {
        var payload = ToolCallViewModel.ToPayload(record);
        var message = new ChatMessage
        {
            Role = MessageRole.Tool,
            Content = JsonSerializer.Serialize(payload, PiableJsonContext.Default.ToolCallPayload),
        };

        Session.Messages.Add(message);
        Items.Add(new ToolCallViewModel(payload, _preferences));

        await _sessions.AppendMessageAsync(Session.Id, message).ConfigureAwait(true);
    }

    /// <summary>把本轮统计写到最后一个助手消息上。</summary>
    private void ApplyTurnStatistics(TurnOutcome outcome, decimal? cost, string model)
    {
        var lastAssistant = Items.OfType<MessageViewModel>().LastOrDefault(m => m.IsAssistant);

        lastAssistant?.ApplyStatistics(
            outcome.Duration, outcome.Usage, cost, model, outcome.Interrupted);
    }

    /// <summary>把历史上的一条消息还原成对应的展示模型。</summary>
    private ChatItemViewModel CreateItem(ChatMessage message)
    {
        if (message.Role != MessageRole.Tool)
        {
            return new MessageViewModel(message, _calculator, _preferences);
        }

        try
        {
            var payload = JsonSerializer.Deserialize(
                message.Content, PiableJsonContext.Default.ToolCallPayload);

            if (payload is not null)
            {
                return new ToolCallViewModel(payload, _preferences);
            }
        }
        catch (JsonException)
        {
            // 数据损坏时退化成一个普通消息条目，总好过整段历史加载不出来
        }

        return new MessageViewModel(message, _calculator, _preferences);
    }

    /// <summary>
    /// 解析本轮可用的工具。工具发现失败不应阻断对话——
    /// 拿不到工具就当作没有工具继续，把原因报到状态栏。
    /// </summary>
    private async Task<IReadOnlyList<ToolDescriptor>> ResolveToolsAsync(Agent agent)
    {
        if (!agent.HasTools)
        {
            return [];
        }

        try
        {
            var resolution = await _toolCatalog
                .ResolveAsync(agent, _generationCts!.Token)
                .ConfigureAwait(true);

            foreach (var warning in resolution.Warnings)
            {
                _status.ReportError(warning);
            }

            return resolution.Tools;
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception ex)
        {
            _status.ReportError($"⚠️ 工具加载失败，本轮以无工具方式继续：{ex.Message}");
            return [];
        }
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
        foreach (var message in Items.OfType<MessageViewModel>())
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
