using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Piable.Models;
// 本文件同时用到两套 ChatMessage：本应用的持久化模型与 Microsoft.Extensions.AI 的传输模型。
// 用别名区分，避免任何一处裸写 ChatMessage 造成歧义。
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ModelChatMessage = Piable.Models.ChatMessage;

namespace Piable.Services;

/// <summary>一次生成所消耗的 Token。</summary>
public sealed record ChatUsage(int? PromptTokens, int? CompletionTokens, int? TotalTokens)
{
    /// <summary>是否为估算值（供应商未返回 usage 时由内容长度推算）。</summary>
    public bool IsEstimated { get; init; }
}

/// <summary>流式生成过程中的一个片段：要么是文本增量，要么是 usage 汇报。</summary>
public sealed record ChatStreamChunk
{
    public string? TextDelta { get; init; }
    public ChatUsage? Usage { get; init; }
}

/// <summary>一次生成请求所需的全部输入。用 record 以便调用方 <c>with</c> 出变体。</summary>
public sealed record ChatRequest
{
    public required ProviderConfig Provider { get; init; }
    public required Agent Agent { get; init; }

    /// <summary>历史消息，不含系统提示词（由编排器自行拼接）。</summary>
    public required IReadOnlyList<ModelChatMessage> History { get; init; }

    /// <summary>覆盖使用的模型；留空则按智能体与供应商配置解析。</summary>
    public string? Model { get; init; }

    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public double? TopP { get; init; }
}

/// <summary>连接测试的结果。</summary>
public sealed record ConnectionTestResult(bool Success, string Message);

/// <summary>对话编排：拼装上下文、调用模型、采集统计（设计文档 4.1、7.4）。</summary>
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
        var options = BuildOptions(request);

        await foreach (var update in client
            .GetStreamingResponseAsync(messages, options, ct)
            .ConfigureAwait(false))
        {
            var text = update.Text;
            if (!string.IsNullOrEmpty(text))
            {
                yield return new ChatStreamChunk { TextDelta = text };
            }

            var usage = TryExtractUsage(update);
            if (usage is not null)
            {
                yield return new ChatStreamChunk { Usage = usage };
            }
        }
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(
        ProviderConfig provider, string? model, CancellationToken ct = default)
    {
        var resolved = string.IsNullOrWhiteSpace(model) ? provider.DefaultModel : model;
        if (string.IsNullOrWhiteSpace(resolved))
        {
            return new ConnectionTestResult(false, "请先选择或填写一个模型。");
        }

        try
        {
            using var client = _factory.Create(provider, resolved);

            var messages = new List<AiChatMessage>
            {
                new(ChatRole.User, "ping"),
            };

            // 只要 1 个 token：目的是验证鉴权与连通性，不是真的取回答
            var options = new ChatOptions { MaxOutputTokens = 1 };

            var response = await client
                .GetResponseAsync(messages, options, ct)
                .ConfigureAwait(false);

            var returnedModel = response.ModelId ?? resolved;
            return new ConnectionTestResult(true, $"✅ 连接成功（{returnedModel}）");
        }
        catch (Exception ex)
        {
            var message = ChatErrorMapper.ToUserMessage(ex, ct.IsCancellationRequested);
            return new ConnectionTestResult(false, message ?? "❌ 连接失败");
        }
    }

    private string ResolveModel(ChatRequest request) =>
        string.IsNullOrWhiteSpace(request.Model)
            ? _factory.ResolveModel(request.Provider, request.Agent)
            : request.Model;

    /// <summary>拼装发给模型的消息：系统提示词在前，其后是历史消息。</summary>
    internal static List<AiChatMessage> BuildMessages(ChatRequest request)
    {
        var messages = new List<AiChatMessage>(request.History.Count + 1);

        if (!string.IsNullOrWhiteSpace(request.Agent.SystemPrompt))
        {
            messages.Add(new AiChatMessage(ChatRole.System, request.Agent.SystemPrompt));
        }

        foreach (var message in request.History)
        {
            // 历史里可能存有 system 消息（例如用户手工注入），保留其原有角色
            messages.Add(new AiChatMessage(MapRole(message.Role), message.Content));
        }

        return messages;
    }

    internal static ChatOptions BuildOptions(ChatRequest request)
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

        return options;
    }

    private static ChatRole MapRole(MessageRole role) => role switch
    {
        MessageRole.System => ChatRole.System,
        MessageRole.User => ChatRole.User,
        MessageRole.Tool => ChatRole.Tool,
        _ => ChatRole.Assistant,
    };

    /// <summary>
    /// 从流式更新里取出 usage。多数兼容 OpenAI 的供应商只在最后一个 chunk 汇报一次，
    /// 因此这里返回 null 是常态而非异常。
    /// </summary>
    private static ChatUsage? TryExtractUsage(ChatResponseUpdate update)
    {
        foreach (var content in update.Contents)
        {
            if (content is not UsageContent usageContent)
            {
                continue;
            }

            var details = usageContent.Details;
            return new ChatUsage(
                ToInt(details.InputTokenCount),
                ToInt(details.OutputTokenCount),
                ToInt(details.TotalTokenCount));
        }

        return null;
    }

    private static int? ToInt(long? value) =>
        value is null ? null : (int)Math.Min(value.Value, int.MaxValue);
}
