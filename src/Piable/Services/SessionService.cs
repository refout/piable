using System.Text.Json;
using Piable.Helpers;
using Piable.Models;
using Piable.Services.Storage;

namespace Piable.Services;

/// <summary>会话与消息的业务操作。</summary>
public interface ISessionService
{
    /// <summary>读取最近的会话摘要，用于侧边栏列表（不含消息正文）。</summary>
    Task<IReadOnlyList<ChatSessionSummary>> GetRecentAsync(int limit = 20, CancellationToken ct = default);

    /// <summary>按关键词搜索会话（标题或消息正文命中）。关键词为空白时返回空列表。</summary>
    Task<IReadOnlyList<ChatSessionSummary>> SearchAsync(
        string keyword, int limit = 50, CancellationToken ct = default);

    /// <summary>加载会话及其全部消息。</summary>
    Task<ChatSession?> LoadAsync(string id, CancellationToken ct = default);

    /// <summary>创建一个空白会话。</summary>
    Task<ChatSession> CreateAsync(string? providerId, string? agentId, CancellationToken ct = default);

    /// <summary>保存会话元数据（标题、智能体、快照等）。</summary>
    Task SaveMetadataAsync(ChatSession session, CancellationToken ct = default);

    /// <summary>用首条用户消息自动命名会话。</summary>
    Task RenameFromFirstMessageAsync(
        ChatSession session, string firstUserMessage, CancellationToken ct = default);

    Task DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>追加一条消息并刷新会话更新时间。</summary>
    Task AppendMessageAsync(string sessionId, ChatMessage message, CancellationToken ct = default);

    /// <summary>导出会话为 Markdown 文本。</summary>
    string ExportAsMarkdown(ChatSession session, Agent? agent);
}

/// <inheritdoc />
public sealed class SessionService : ISessionService
{
    private readonly SessionRepository _repository;

    public SessionService(SessionRepository repository) => _repository = repository;

    public Task<IReadOnlyList<ChatSessionSummary>> GetRecentAsync(
        int limit = 20, CancellationToken ct = default) =>
        GetAllInternalAsync(limit, ct);

    private async Task<IReadOnlyList<ChatSessionSummary>> GetAllInternalAsync(int limit, CancellationToken ct)
    {
        var summaries = await _repository.GetSummariesAsync(limit, ct).ConfigureAwait(false);
        return summaries;
    }

    public async Task<IReadOnlyList<ChatSessionSummary>> SearchAsync(
        string keyword, int limit = 50, CancellationToken ct = default)
    {
        // 空白关键词直接返回空：LIKE '%%' 会把所有有消息的会话都捞出来，
        // 看上去像"搜索什么都搜得到"，实际上等于没搜索。
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return [];
        }

        return await _repository.SearchAsync(keyword.Trim(), limit, ct).ConfigureAwait(false);
    }

    public Task<ChatSession?> LoadAsync(string id, CancellationToken ct = default) =>
        _repository.GetByIdAsync(id, ct);

    public async Task<ChatSession> CreateAsync(
        string? providerId, string? agentId, CancellationToken ct = default)
    {
        var session = new ChatSession
        {
            Title = SessionTitleGenerator.DefaultTitle,
            ProviderId = providerId,
            AgentId = agentId,
        };

        await _repository.UpsertAsync(session, ct).ConfigureAwait(false);
        return session;
    }

    public Task SaveMetadataAsync(ChatSession session, CancellationToken ct = default)
    {
        session.UpdatedAt = DateTimeOffset.Now;
        return _repository.UpsertAsync(session, ct);
    }

    public async Task RenameFromFirstMessageAsync(
        ChatSession session, string firstUserMessage, CancellationToken ct = default)
    {
        // 只在仍是默认标题时自动命名，避免覆盖用户手动改过的名字
        if (!string.Equals(session.Title, SessionTitleGenerator.DefaultTitle, StringComparison.Ordinal))
        {
            return;
        }

        session.Title = SessionTitleGenerator.Generate(firstUserMessage);
        await SaveMetadataAsync(session, ct).ConfigureAwait(false);
    }

    public Task DeleteAsync(string id, CancellationToken ct = default) =>
        _repository.DeleteAsync(id, ct);

    public Task AppendMessageAsync(
        string sessionId, ChatMessage message, CancellationToken ct = default) =>
        _repository.AppendMessageAsync(sessionId, message, ct);

    public string ExportAsMarkdown(ChatSession session, Agent? agent)
    {
        var builder = new System.Text.StringBuilder();

        builder.Append("# ").AppendLine(session.Title);
        builder.AppendLine();
        builder.Append(Loc.Get("Export.Agent")).AppendLine(agent?.Name ?? session.AgentId ?? Loc.Get("Common.DeletedParen"));
        builder.Append(Loc.Get("Export.Model")).AppendLine(session.ModelUsed ?? "—");
        builder.Append(Loc.Get("Export.CreatedAt")).AppendLine(session.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        builder.Append(Loc.Get("Export.MessageCount")).AppendLine(session.Messages.Count.ToString());
        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine();

        foreach (var message in session.Messages)
        {
            builder.Append("## ").AppendLine(DescribeRole(message.Role));
            builder.AppendLine();
            builder.AppendLine(message.Content);

            if (message.TotalTokens is not null)
            {
                builder.AppendLine();
                builder.Append("> ").Append(message.DurationMs is { } ms ? $"{ms / 1000.0:0.0}s · " : string.Empty)
                       .Append(message.TotalTokens).AppendLine(" tokens");
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string DescribeRole(MessageRole role) => role switch
    {
        MessageRole.User => Loc.Get("Export.RoleUser"),
        MessageRole.Assistant => Loc.Get("Export.RoleAssistant"),
        MessageRole.System => Loc.Get("Export.RoleSystem"),
        MessageRole.Tool => Loc.Get("Export.RoleTool"),
        _ => role.ToString(),
    };

    /// <summary>把智能体配置序列化为会话快照，供历史回放时还原当时的设置。</summary>
    public static string? CreateAgentSnapshot(Agent? agent) =>
        agent is null ? null : JsonSerializer.Serialize(agent, PiableJsonContext.Default.Agent);

    /// <summary>从会话快照还原智能体配置；快照损坏时返回 null。</summary>
    public static Agent? ParseAgentSnapshot(string? snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(snapshot, PiableJsonContext.Default.Agent);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
