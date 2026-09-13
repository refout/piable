using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Piable.Helpers;
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
    private readonly IAgentResourceService _resources;
    private readonly ITokenCostCalculator _calculator;
    private readonly UserPreferences _preferences;
    private readonly IStatusReporter _status;

    private CancellationTokenSource? _generationCts;

    /// <summary>区分"用户点了停止"与"请求超时"——两者都表现为取消异常。</summary>
    private bool _userCancelled;

    /// <summary>当前挂起等待用户决定的工具确认卡片。</summary>
    private ToolConfirmationViewModel? _pendingConfirmation;

    /// <summary>
    /// 用户勾选了"本次会话内不再询问"之后为 true。
    /// 只在内存中存续，不写进偏好——那等于把一次性授权又变回永久授权。
    /// </summary>
    private bool _trustDangerousTools;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _inputText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isGenerating;

    /// <summary>当前会话使用的智能体。改动会即时生效于下一轮生成。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgentButtonText))]
    [NotifyPropertyChangedFor(nameof(AgentDescription))]
    [NotifyPropertyChangedFor(nameof(HasAgentDescription))]
    [NotifyPropertyChangedFor(nameof(EffectiveModel))]
    [NotifyPropertyChangedFor(nameof(ModelButtonText))]
    private Agent? _selectedAgent;

    /// <summary>
    /// 本次会话内覆盖使用的模型；null 表示跟随智能体 / 供应商默认值。
    /// 与采样参数一样只在会话内存续，不写回供应商配置——
    /// 在对话里临时换个模型试试是很常见的动作，把它写回配置会让"默认模型"变得难以预期。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveModel))]
    [NotifyPropertyChangedFor(nameof(ModelButtonText))]
    [NotifyPropertyChangedFor(nameof(HasModelOverride))]
    private string? _modelOverride;

    // 以下三个是本次会话内的临时覆盖，不写回智能体配置；
    // 切换智能体时会重新取该智能体的默认值。

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ParameterSummary))]
    private double _temperature = 0.7;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ParameterSummary))]
    private int _maxTokens = 2048;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ParameterSummary))]
    private double _topP = 1.0;

    public ChatSessionViewModel(
        ChatSession session,
        IReadOnlyList<Agent> availableAgents,
        ProviderConfig? provider,
        ISessionService sessions,
        IAgentOrchestrator orchestrator,
        IToolCatalog toolCatalog,
        IAgentResourceService resources,
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
        _resources = resources;
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
    [NotifyPropertyChangedFor(nameof(EffectiveModel))]
    [NotifyPropertyChangedFor(nameof(ModelButtonText))]
    private ProviderConfig? _provider;

    public string Title => Session.Title;

    /// <summary>当前供应商可选的模型。供应商一变就重建，切换会话时由主窗口赋值触发。</summary>
    public ObservableCollection<string> AvailableModels { get; } = [];

    /// <summary>本次实际会使用的模型：会话内覆盖优先，其次智能体指定，最后供应商默认。</summary>
    public string EffectiveModel => ResolveModel(SelectedAgent ?? new Agent { SystemPrompt = string.Empty });

    /// <summary>模型按钮上的文字：<c>gpt-4o-mini</c>。</summary>
    public string ModelButtonText =>
        string.IsNullOrWhiteSpace(EffectiveModel) ? Loc.Get("Chat.NoModel") : EffectiveModel;

    /// <summary>是否正在覆盖模型。决定"跟随默认"按钮能不能点。</summary>
    public bool HasModelOverride => !string.IsNullOrWhiteSpace(ModelOverride);

    /// <summary>放弃会话内的模型覆盖，回到智能体 / 供应商默认。</summary>
    [RelayCommand]
    private void ResetModel() => ModelOverride = null;

    /// <summary>
    /// "当前跟随默认：gpt-4o-mini" 这类整句。
    /// 没有跟随默认时不显示——文案里带占位符，交给 XAML 拼会写死语序，换了语言就别扭。
    /// </summary>
    public string ModelFollowDefaultHint =>
        HasModelOverride ? string.Empty : Loc.Get("Chat.FollowDefaultHint", EffectiveModel);

    partial void OnProviderChanged(ProviderConfig? value) => RefreshAvailableModels();

    private void RefreshAvailableModels()
    {
        AvailableModels.Clear();

        var models = new List<string>();
        foreach (var model in Provider?.Models ?? [])
        {
            if (!string.IsNullOrWhiteSpace(model) && !models.Contains(model))
            {
                models.Add(model);
            }
        }

        // 供应商可能把默认模型配在了列表之外（手动填的），
        // 不补进去的话界面上"正在用哪个模型"就在下拉里找不到对应项。
        var current = EffectiveModel;
        if (!string.IsNullOrWhiteSpace(current) && !models.Contains(current))
        {
            models.Insert(0, current);
        }

        foreach (var model in models)
        {
            AvailableModels.Add(model);
        }
    }

    /// <summary>输入区工具行按钮上的文字：<c>代码助手</c>。</summary>
    public string AgentButtonText =>
        SelectedAgent is null ? Loc.Get("Chat.NoAgent") : SelectedAgent.Name;

    /// <summary>智能体的描述，显示在选择面板里。</summary>
    public string? AgentDescription => SelectedAgent?.Description;

    public bool HasAgentDescription => !string.IsNullOrWhiteSpace(SelectedAgent?.Description);

    /// <summary>参数摘要，作为按钮的悬停提示，不必展开面板就能看到当前取值。</summary>
    public string ParameterSummary =>
        $"Temperature {Temperature:0.##}  ·  Top P {TopP:0.##}  ·  Max Tokens {MaxTokens}";

    /// <summary>放弃本次会话的参数改动，重新取智能体（或供应商）的默认值。</summary>
    [RelayCommand]
    private void ResetParameters()
    {
        ApplyParameterDefaults();
        OnPropertyChanged(nameof(ParameterSummary));
    }

    /// <summary>
    /// 标题栏统计：<c>12条 · 2.3K · 8.7s</c>。
    /// 不含智能体名与模型名——输入区的工具按钮已经显示了，重复既冗余又占用本就紧张的标题栏宽度。
    /// </summary>
    public string HeaderStatistics
    {
        get
        {
            var messages = Items.OfType<MessageViewModel>().ToList();
            var tokens = messages.Sum(m => (long)(m.TotalTokens ?? 0));
            var duration = messages.Sum(m => m.DurationMs ?? 0);

            return Loc.Get("Chat.StatsSummary", messages.Count,
                _calculator.FormatTokens(tokens), _calculator.FormatDuration(duration));
        }
    }

    /// <summary>
    /// 标题栏统计是否显示。
    /// 既要服从"显示统计信息"偏好，也要避开空会话——那里显示"0条 · 0 · 0.0s"纯属噪音。
    /// </summary>
    public bool ShouldShowHeaderStatistics =>
        _preferences.ShowStatistics && Items.OfType<MessageViewModel>().Any();

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
            _status.ReportError(Loc.Get("Chat.NeedProvider"));
            return;
        }

        var agent = SelectedAgent ?? new Agent { SystemPrompt = string.Empty };
        var model = ResolveModel(agent);

        if (string.IsNullOrWhiteSpace(model))
        {
            _status.ReportError(Loc.Get("Chat.NeedModel"));
            return;
        }

        InputText = string.Empty;
        IsGenerating = true;
        _userCancelled = false;
        _status.SetBusy(Loc.Get("Chat.Generating"));

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

        // 用户刚发出消息，立刻把列表拉到底部，否则新消息会落在可视区之外
        // （助手首字可能要等好几百毫秒的网络往返，这段时间用户看不到自己刚发的消息）。
        if (IsActive)
        {
            ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
        }

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
        var resourceContext = await ResolveResourceContextAsync(agent).ConfigureAwait(true);

        var request = new ChatRequest
        {
            Provider = Provider!,
            Agent = agent,
            History = history,
            ResourceContext = resourceContext,
            Model = model,
            Temperature = Temperature,
            MaxTokens = MaxTokens,
            TopP = TopP,
            Tools = tools,
            AllowDangerousTools = agent.AllowDangerousTools,
            MaxToolRounds = _preferences.MaxToolRounds,
            ThinkingEnabled = _preferences.ThinkingEnabled,

            // 未开启逐次确认时不挂回调，编排器就退回"一次授权、全程放行"的行为
            ConfirmDangerousTool = _preferences.ConfirmDangerousTools
                ? ConfirmDangerousToolAsync
                : null,
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

    /// <summary>
    /// 危险工具执行前的确认。把卡片放进对话流，等用户点按钮。
    ///
    /// 刻意不把 <paramref name="ct"/> 传给等待：那个 token 是整轮生成的超时，
    /// 而人读一遍参数再决定通常要几秒，让"想太久"变成"请求超时"毫无道理。
    /// 超时在等待期间暂停，用户点完再从头计时。
    /// </summary>
    private async Task<bool> ConfirmDangerousToolAsync(
        ToolConfirmationRequest request, CancellationToken ct)
    {
        if (_trustDangerousTools)
        {
            return true;
        }

        var confirmation = new ToolConfirmationViewModel(request);
        _pendingConfirmation = confirmation;

        PauseRequestTimeout();
        Items.Add(confirmation);
        ScrollToEndRequested?.Invoke(this, EventArgs.Empty);

        try
        {
            var allowed = await confirmation.Completion.ConfigureAwait(true);
            if (allowed && confirmation.TrustForThisSession)
            {
                _trustDangerousTools = true;
            }

            return allowed;
        }
        finally
        {
            _pendingConfirmation = null;
            ResumeRequestTimeout();
        }
    }

    /// <summary>暂停整轮超时计时。传 Infinite 会取消此前排定的取消。</summary>
    private void PauseRequestTimeout() =>
        _generationCts?.CancelAfter(Timeout.InfiniteTimeSpan);

    private void ResumeRequestTimeout() =>
        _generationCts?.CancelAfter(
            TimeSpan.FromSeconds(Math.Max(1, _preferences.RequestTimeoutSeconds)));

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

                if (chunk.ReasoningDelta is not null)
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

                    current.AppendThinking(chunk.ReasoningDelta);

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
            _status.ReportError(Loc.Get("Error.Timeout"));
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

        // 正文与思考都为空才丢弃：纯推理消息（有思考、无最终回答）也要保留。
        if (model.Content.Length == 0 && string.IsNullOrEmpty(model.ThinkingContent))
        {
            Items.Remove(view);
            return;
        }

        if (interrupted)
        {
            // 设计文档 4.2：在末尾标记已中断
            view.AppendText(Loc.Get("Chat.InterruptedMark"));
        }

        // 纯推理消息（只有思考、没有最终回答）也要把状态切到"已思考"
        view.EndThinking();

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
        Items.Add(new ToolCallViewModel(payload));

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
                return new ToolCallViewModel(payload);
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
            _status.ReportError(Loc.Get("Chat.ToolLoadFailed", ex.Message));
            return [];
        }
    }

    /// <summary>
    /// 读取智能体挂载的 MCP 资源。
    /// 读不到不阻断对话——资源只是参考材料，缺了它模型照样能回答，
    /// 而为了拿一份参考材料把整轮对话卡住是明显不划算的。
    /// </summary>
    private async Task<string?> ResolveResourceContextAsync(Agent agent)
    {
        if (agent.McpResourceUris.Count == 0)
        {
            return null;
        }

        try
        {
            var resolution = await _resources
                .ResolveAsync(agent, _generationCts!.Token)
                .ConfigureAwait(true);

            foreach (var warning in resolution.Warnings)
            {
                _status.ReportError(warning);
            }

            return resolution.Text.Length == 0 ? null : resolution.Text;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _status.ReportError(Loc.Get("Chat.ResourceLoadFailed", ex.Message));
            return null;
        }
    }

    private string ResolveModel(Agent agent)
    {
        // 会话内的手动选择优先于一切：用户在输入框旁边选了模型，就该用它
        if (!string.IsNullOrWhiteSpace(ModelOverride))
        {
            return ModelOverride;
        }

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

        // 正在等用户确认时点停止：先把等待放掉，否则生成会一直卡在确认上
        _pendingConfirmation?.Abandon();
        _generationCts?.Cancel();
    }

    /// <summary>
    /// 放弃尚未决断的确认。主窗口切换会话时调用——
    /// 原会话已经不在眼前，留一张永远没人点的卡片会把那条生成永久挂住。
    /// </summary>
    public void AbandonPendingConfirmation() => _pendingConfirmation?.Abandon();

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
    public void NotifyHeaderChanged()
    {
        OnPropertyChanged(nameof(HeaderStatistics));
        OnPropertyChanged(nameof(ShouldShowHeaderStatistics));
    }

    /// <summary>
    /// 导出当前会话为 Markdown 文本。
    /// 文件对话框与写盘属于视图的事，这里只负责产出内容——
    /// 这样导出逻辑本身可以脱离界面测试。
    /// </summary>
    public string ExportAsMarkdown() => _sessions.ExportAsMarkdown(Session, SelectedAgent);

    /// <summary>
    /// 手动重命名并落库。
    /// 空标题不生效——留一个没有名字的会话，比保留原来的名字更糟。
    /// </summary>
    public async Task RenameAsync(string? title)
    {
        var trimmed = title?.Trim() ?? string.Empty;

        if (trimmed.Length == 0
            || string.Equals(trimmed, Session.Title, StringComparison.Ordinal))
        {
            return;
        }

        Session.Title = trimmed;
        await _sessions.SaveMetadataAsync(Session, CancellationToken.None).ConfigureAwait(true);

        OnPropertyChanged(nameof(Title));
        TitleChanged?.Invoke(this, EventArgs.Empty);
    }
}
