using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Piable.Helpers;
using Piable.Models;
using Piable.Services.Tools;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
// 本文件同时用到两套 ChatMessage：本应用的持久化模型与 Microsoft.Extensions.AI 的传输模型。
// 用别名区分，避免任何一处裸写 ChatMessage 造成歧义。
using ModelChatMessage = Piable.Models.ChatMessage;

namespace Piable.Services;

/// <summary>一次生成所消耗的 Token。</summary>
public sealed record ChatUsage(int? PromptTokens, int? CompletionTokens, int? TotalTokens)
{
    /// <summary>是否为估算值（供应商未返回 usage 时由内容长度推算）。</summary>
    public bool IsEstimated { get; init; }
}

/// <summary>工具调用的结局。</summary>
public enum ToolInvocationStatus
{
    Succeeded,
    Failed,

    /// <summary>被本地的危险工具策略拒绝，未真正执行。</summary>
    Denied,

    /// <summary>用户在本轮的逐次确认中拒绝执行。</summary>
    Declined,
}

/// <summary>一次危险工具执行的确认请求，交给调用方决定放行还是拒绝。</summary>
/// <param name="ToolName">模型实际调用的工具名。</param>
/// <param name="DisplayName">展示用名称（不带 MCP 服务器前缀）。</param>
/// <param name="SourceLabel">来源说明，如「技能 · 执行命令」。</param>
/// <param name="ArgumentsText">模型给出的参数，已摊平成可读文本。</param>
/// <param name="Description">工具自身的描述，没有则为 null。</param>
public sealed record ToolConfirmationRequest(
    string ToolName,
    string DisplayName,
    string SourceLabel,
    string ArgumentsText,
    string? Description);

/// <summary>一次工具调用的完整记录，供界面展示与审计。</summary>
public sealed record ToolInvocationRecord
{
    public required string ToolName { get; init; }

    /// <summary>展示用名称（原始工具名，不带服务器前缀）。</summary>
    public required string DisplayName { get; init; }

    public required string SourceLabel { get; init; }

    public required ToolRisk Risk { get; init; }

    /// <summary>模型给出的参数，已摊平成可读文本。</summary>
    public required string ArgumentsText { get; init; }

    public required ToolInvocationStatus Status { get; init; }

    /// <summary>回填给模型的文本。</summary>
    public required string ResultPayload { get; init; }

    /// <summary>第几轮工具调用（从 1 开始）。</summary>
    public required int Iteration { get; init; }

    public TimeSpan Duration { get; init; }
}

/// <summary>流式生成过程中的一个片段。</summary>
public sealed record ChatStreamChunk
{
    /// <summary>模型产出的文本增量。</summary>
    public string? TextDelta { get; init; }

    /// <summary>供应商汇报的累计用量（含之前的工具轮次）。</summary>
    public ChatUsage? Usage { get; init; }

    /// <summary>一次工具调用已完成（含被拒绝的情形）。</summary>
    public ToolInvocationRecord? ToolCall { get; init; }

    /// <summary>模型推理/思考内容的增量（扩展思考模式）。可能为 null。</summary>
    public string? ReasoningDelta { get; init; }
}

/// <summary>一次生成请求所需的全部输入。用 record 以便调用方 <c>with</c> 出变体。</summary>
public sealed record ChatRequest
{
    public required ProviderConfig Provider { get; init; }
    public required Agent Agent { get; init; }

    /// <summary>历史消息，不含系统提示词（由编排器自行拼接）。</summary>
    public required IReadOnlyList<ModelChatMessage> History { get; init; }

    /// <summary>
    /// 附加上下文（如智能体挂载的 MCP 资源），拼在系统提示词之后。
    /// 单独成段是为了让模型分清哪部分是角色设定、哪部分是参考材料。
    /// </summary>
    public string? ResourceContext { get; init; }

    /// <summary>覆盖使用的模型；留空则按智能体与供应商配置解析。</summary>
    public string? Model { get; init; }

    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public double? TopP { get; init; }

    /// <summary>本次可用的工具。为空表示不启用工具调用。</summary>
    public IReadOnlyList<ToolDescriptor> Tools { get; init; } = [];

    /// <summary>是否允许执行被标记为危险的工具。</summary>
    public bool AllowDangerousTools { get; init; }

    /// <summary>是否开启思考模式（扩展推理）。开启时捕获并展示推理内容，
    /// 并对支持的模型通过 <c>reasoning_effort</c> 请求推理。</summary>
    public bool ThinkingEnabled { get; init; }

    /// <summary>
    /// 危险工具执行前的逐次确认。返回 true 才执行，false 视为用户拒绝。
    ///
    /// 为 null 时退回"按智能体一次性授权"：开启 <see cref="AllowDangerousTools"/> 即全部放行。
    /// 放在请求里而不是编排器上，是因为要不要问、怎么问是界面层的事，
    /// 编排器只负责在正确的时机调用它。
    /// </summary>
    public Func<ToolConfirmationRequest, CancellationToken, Task<bool>>? ConfirmDangerousTool { get; init; }

    /// <summary>工具调用的最大轮数，防止模型陷入无限调用。</summary>
    public int MaxToolRounds { get; init; } = 5;
}

/// <summary>连接测试的结果。</summary>
public sealed record ConnectionTestResult(bool Success, string Message);

/// <summary>对话编排：拼装上下文、调用模型、执行工具、采集统计（设计文档 4.1、7.4、8.4）。</summary>
public interface IAgentOrchestrator
{
    /// <summary>流式生成。异常由调用方捕获后交给 <see cref="ChatErrorMapper"/> 处理。</summary>
    IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, CancellationToken ct);

    /// <summary>用一次极短的请求验证供应商配置是否可用。</summary>
    Task<ConnectionTestResult> TestConnectionAsync(
        ProviderConfig provider, string? model, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class AgentOrchestrator : IAgentOrchestrator
{
    private readonly IChatClientFactory _factory;

    public AgentOrchestrator(IChatClientFactory factory) => _factory = factory;

    public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var model = ResolveModel(request);
        using var client = _factory.Create(request.Provider, model);

        var messages = BuildMessages(request);
        var descriptors = request.Tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

        var toolsEnabled = request.Tools.Count > 0;
        var maxRounds = Math.Max(0, request.MaxToolRounds);

        // 累计用量：工具调用会产生多次请求，界面要展示的是这一整轮的总开销
        var promptTotal = 0;
        var completionTotal = 0;
        var hasUsage = false;

        // 轮数用尽后仍要给出回答，因此循环上界是 maxRounds + 1，
        // 且最后一轮不带工具——模型只能产出文本，不会再把对话悬在半空。
        for (var iteration = 0; iteration <= (toolsEnabled ? maxRounds : 0); iteration++)
        {
            var allowToolsThisRound = toolsEnabled && iteration < maxRounds;
            var options = BuildOptions(request, allowToolsThisRound ? request.Tools : null);

            // 思考模式：仅对原生支持推理的模型显式请求 reasoning_effort，
            // 其余模型（含 gpt-4o 这类不思考的）不发该参数，避免 API 报错。
            // DeepSeek-R1 / Qwen-QwQ 等会自动在流里返回推理内容，无需此处请求。
            if (request.ThinkingEnabled && IsReasoningModel(model))
            {
                options.AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["reasoning_effort"] = "medium",
                };
            }

            var toolCalls = new List<FunctionCallContent>();
            var assistantContents = new List<AIContent>();

            await foreach (var update in client
                .GetStreamingResponseAsync(messages, options, ct)
                .ConfigureAwait(false))
            {
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case FunctionCallContent call:
                            toolCalls.Add(call);
                            break;
                        case UsageContent usage:
                            promptTotal += (int)(usage.Details.InputTokenCount ?? 0);
                            completionTotal += (int)(usage.Details.OutputTokenCount ?? 0);
                            hasUsage = true;
                            break;
                        case TextReasoningContent reasoning when request.ThinkingEnabled:
                            // 扩展思考：把推理增量单独成块转发，不混入最终回答正文。
                            if (!string.IsNullOrEmpty(reasoning.Text))
                            {
                                yield return new ChatStreamChunk { ReasoningDelta = reasoning.Text };
                            }
                            break;
                    }
                }

                if (update.Contents.Count > 0)
                {
                    assistantContents.AddRange(update.Contents);
                }

                if (!string.IsNullOrEmpty(update.Text))
                {
                    yield return new ChatStreamChunk { TextDelta = update.Text };
                }
            }

            if (hasUsage)
            {
                yield return new ChatStreamChunk
                {
                    Usage = new ChatUsage(promptTotal, completionTotal, promptTotal + completionTotal),
                };
            }

            if (toolCalls.Count == 0)
            {
                yield break;
            }

            // 带工具调用的助手消息必须先入上下文，否则工具结果会缺少对应的请求
            messages.Add(new AiChatMessage(ChatRole.Assistant, assistantContents));

            foreach (var call in toolCalls)
            {
                ct.ThrowIfCancellationRequested();

                var record = await InvokeToolAsync(
                    call, descriptors, request.AllowDangerousTools,
                    request.ConfirmDangerousTool, iteration + 1, ct)
                    .ConfigureAwait(false);

                yield return new ChatStreamChunk { ToolCall = record };

                messages.Add(new AiChatMessage(
                    ChatRole.Tool,
                    [new FunctionResultContent(call.CallId, record.ResultPayload)]));
            }
        }
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(
        ProviderConfig provider, string? model, CancellationToken ct = default)
    {
        var resolved = string.IsNullOrWhiteSpace(model) ? provider.DefaultModel : model;
        if (string.IsNullOrWhiteSpace(resolved))
        {
            return new ConnectionTestResult(false, Loc.Get("Provider.PickModelFirst"));
        }

        try
        {
            using var client = _factory.Create(provider, resolved);

            var messages = new List<AiChatMessage> { new(ChatRole.User, "ping") };

            // 只要 1 个 token：目的是验证鉴权与连通性，不是真的取回答
            var options = new ChatOptions { MaxOutputTokens = 1 };

            var response = await client
                .GetResponseAsync(messages, options, ct)
                .ConfigureAwait(false);

            var returnedModel = response.ModelId ?? resolved;
            return new ConnectionTestResult(true, Loc.Get("Provider.Connected", returnedModel));
        }
        catch (Exception ex)
        {
            var message = ChatErrorMapper.ToUserMessage(ex, ct.IsCancellationRequested);
            return new ConnectionTestResult(false, message ?? Loc.Get("Provider.ConnectFailed"));
        }
    }

    /// <summary>
    /// 执行一次工具调用。
    ///
    /// 任何失败都以"错误结果"回填给模型，而不是向上抛出结束整轮对话——
    /// 模型看到错误后往往能自行改参数或换工具，直接中断反而让用户拿不到任何回答。
    /// 唯一的例外是用户主动取消，那必须立刻停下。
    /// </summary>
    private static async Task<ToolInvocationRecord> InvokeToolAsync(
        FunctionCallContent call,
        IReadOnlyDictionary<string, ToolDescriptor> descriptors,
        bool allowDangerousTools,
        Func<ToolConfirmationRequest, CancellationToken, Task<bool>>? confirmDangerousTool,
        int iteration,
        CancellationToken ct)
    {
        var argumentsText = FormatArguments(call.Arguments);

        if (!descriptors.TryGetValue(call.Name, out var descriptor))
        {
            // 模型调用了一个未被提供的工具。常见于上一轮的工具列表与这一轮不一致。
            return new ToolInvocationRecord
            {
                ToolName = call.Name,
                DisplayName = call.Name,
                SourceLabel = Loc.Get("Common.Unknown"),
                Risk = ToolRisk.Safe,
                ArgumentsText = argumentsText,
                Status = ToolInvocationStatus.Failed,
                ResultPayload = Loc.Get("Tool.ErrorNotFound", call.Name),
                Iteration = iteration,
            };
        }

        if (descriptor.Risk == ToolRisk.Dangerous && !allowDangerousTools)
        {
            return new ToolInvocationRecord
            {
                ToolName = descriptor.Name,
                DisplayName = descriptor.OriginalName ?? descriptor.Name,
                SourceLabel = descriptor.SourceLabel,
                Risk = descriptor.Risk,
                ArgumentsText = argumentsText,
                Status = ToolInvocationStatus.Denied,
                ResultPayload = Loc.Get("Tool.ErrorDangerous"),
                Iteration = iteration,
            };
        }

        // 逐次确认。即便智能体已获得授权，也仍然逐次询问——
        // "这个智能体可以执行危险工具"与"这一次可以执行"是两件事，
        // 模型完全可能被某条工具返回值里的提示注入诱导去调用它不该调用的东西。
        if (descriptor.Risk == ToolRisk.Dangerous && confirmDangerousTool is not null)
        {
            var approved = await confirmDangerousTool(
                new ToolConfirmationRequest(
                    descriptor.Name,
                    descriptor.OriginalName ?? descriptor.Name,
                    descriptor.SourceLabel,
                    argumentsText,
                    descriptor.Description),
                ct).ConfigureAwait(false);

            if (!approved)
            {
                return new ToolInvocationRecord
                {
                    ToolName = descriptor.Name,
                    DisplayName = descriptor.OriginalName ?? descriptor.Name,
                    SourceLabel = descriptor.SourceLabel,
                    Risk = descriptor.Risk,
                    ArgumentsText = argumentsText,
                    Status = ToolInvocationStatus.Declined,
                    ResultPayload = Loc.Get("Tool.ErrorDeclined"),
                    Iteration = iteration,
                };
            }
        }

        var started = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var arguments = new AIFunctionArguments(
                call.Arguments ?? new Dictionary<string, object?>());

            var result = descriptor.Tool is AIFunction function
                ? await function.InvokeAsync(arguments, ct).ConfigureAwait(false)
                : null;

            started.Stop();

            return new ToolInvocationRecord
            {
                ToolName = descriptor.Name,
                DisplayName = descriptor.OriginalName ?? descriptor.Name,
                SourceLabel = descriptor.SourceLabel,
                Risk = descriptor.Risk,
                ArgumentsText = argumentsText,
                Status = ToolInvocationStatus.Succeeded,
                ResultPayload = result?.ToString() ?? Loc.Get("Tool.NoOutput"),
                Iteration = iteration,
                Duration = started.Elapsed,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            started.Stop();

            return new ToolInvocationRecord
            {
                ToolName = descriptor.Name,
                DisplayName = descriptor.OriginalName ?? descriptor.Name,
                SourceLabel = descriptor.SourceLabel,
                Risk = descriptor.Risk,
                ArgumentsText = argumentsText,
                Status = ToolInvocationStatus.Failed,
                ResultPayload = Loc.Get("Tool.ErrorFailed", ex.Message),
                Iteration = iteration,
                Duration = started.Elapsed,
            };
        }
    }

    /// <summary>
    /// 把参数摊平成可读文本。
    /// 不用 JSON 序列化是因为参数值是 <c>object</c>，走反射序列化在禁用反射的模式下会失败；
    /// 而且键值对形式在界面上比 JSON 更好读。
    /// </summary>
    private static string FormatArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return Loc.Get("Common.NoParametersParen");
        }

        var builder = new StringBuilder();
        foreach (var (key, value) in arguments)
        {
            if (builder.Length > 0)
            {
                builder.Append("，");
            }

            builder.Append(key).Append('=').Append(Describe(value));
        }

        return builder.ToString();
    }

    private static string Describe(object? value) => value switch
    {
        null => "null",
        string s => s,
        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } e =>
            e.GetString() ?? string.Empty,
        System.Text.Json.JsonElement e => e.ToString(),
        _ => value.ToString() ?? string.Empty,
    };

    private string ResolveModel(ChatRequest request) =>
        string.IsNullOrWhiteSpace(request.Model)
            ? _factory.ResolveModel(request.Provider, request.Agent)
            : request.Model;

    /// <summary>拼装发给模型的消息：系统提示词在前，其后是历史消息。</summary>
    internal static List<AiChatMessage> BuildMessages(ChatRequest request)
    {
        var messages = new List<AiChatMessage>(request.History.Count + 1);

        // 资源上下文接在系统提示词之后合为一条 system 消息。
        // 拆成两条也能工作，但部分供应商只取第一条 system 消息，合并更稳妥。
        var systemPrompt = request.Agent.SystemPrompt ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(request.ResourceContext))
        {
            systemPrompt = string.IsNullOrWhiteSpace(systemPrompt)
                ? request.ResourceContext
                : systemPrompt.TrimEnd() + "\n\n" + request.ResourceContext;
        }

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new AiChatMessage(ChatRole.System, systemPrompt));
        }

        foreach (var message in request.History)
        {
            // 历史里可能存有 system 消息（例如用户手工注入），保留其原有角色
            messages.Add(new AiChatMessage(MapRole(message.Role), message.Content));
        }

        return messages;
    }

    internal static ChatOptions BuildOptions(ChatRequest request, IReadOnlyList<ToolDescriptor>? tools)
    {
        var options = new ChatOptions();

        if (request.Temperature is { } temperature)
        {
            options.Temperature = (float)temperature;
        }

        if (request.MaxTokens is { } maxTokens)
        {
            options.MaxOutputTokens = maxTokens;
        }

        if (request.TopP is { } topP)
        {
            options.TopP = (float)topP;
        }

        if (tools is { Count: > 0 })
        {
            options.Tools = [.. tools.Select(t => t.Tool)];
            options.ToolMode = ChatToolMode.Auto;
        }

        return options;
    }

    /// <summary>
    /// 判断模型是否原生支持、且需要显式请求推理。
    /// OpenAI o 系列靠 <c>reasoning_effort</c> 开启；DeepSeek-R1 / Qwen-QwQ 等会自动返回推理，
    /// 命中它们也无害（多带一个被忽略的参数），但主要为了挡住 gpt-4o 这类不思考的模型。
    /// 这是启发式，覆盖常见命名；自定义端点的非常规命名可能漏判，后果仅是"开了思考却不推理"。
    /// </summary>
    private static bool IsReasoningModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        return model.StartsWith("o", StringComparison.OrdinalIgnoreCase)
            || model.Contains("reasoner", StringComparison.OrdinalIgnoreCase)
            || model.Contains("qwq", StringComparison.OrdinalIgnoreCase);
    }

    private static ChatRole MapRole(MessageRole role) => role switch
    {
        MessageRole.System => ChatRole.System,
        MessageRole.User => ChatRole.User,
        MessageRole.Tool => ChatRole.Tool,
        _ => ChatRole.Assistant,
    };
}
