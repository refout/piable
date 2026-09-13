using Microsoft.Data.Sqlite;
using Piable.Models;

namespace Piable.Services.Storage;

/// <summary>会话与消息的持久化。</summary>
public sealed class SessionRepository
{
    private readonly PiableDatabase _database;

    public SessionRepository(PiableDatabase database) => _database = database;

    /// <summary>
    /// 读取会话摘要列表。侧边栏只需要标题与统计，不加载消息正文，
    /// 以支撑"启动时只取最近若干条、点击后再加载全文"的懒加载策略。
    /// </summary>
    public async Task<List<ChatSessionSummary>> GetSummariesAsync(
        int limit = 20, CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.Id, s.Title, s.AgentId, s.ProviderId, s.ModelUsed, s.CreatedAt, s.UpdatedAt,
                   COUNT(m.Id) AS MessageCount,
                   COALESCE(SUM(m.TotalTokens), 0) AS TotalTokens
            FROM Sessions s
            LEFT JOIN Messages m ON m.SessionId = s.Id
            GROUP BY s.Id
            ORDER BY s.UpdatedAt DESC
            LIMIT $limit;
            """;
        command.AddParam("$limit", limit);

        var result = new List<ChatSessionSummary>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new ChatSessionSummary
            {
                Id = reader.ReadString("Id"),
                Title = reader.ReadString("Title"),
                AgentId = reader.ReadNullableString("AgentId"),
                ProviderId = reader.ReadNullableString("ProviderId"),
                ModelUsed = reader.ReadNullableString("ModelUsed"),
                MessageCount = reader.ReadInt32("MessageCount"),
                TotalTokens = reader.ReadInt64("TotalTokens"),
                CreatedAt = reader.ReadDateTimeOffset("CreatedAt"),
                UpdatedAt = reader.ReadDateTimeOffset("UpdatedAt"),
            });
        }

        return result;
    }

    /// <summary>
    /// 按关键词搜索会话：标题或任意一条消息正文包含关键词即命中。
    ///
    /// 标题与正文分开统计命中数，是因为界面要能区分"只是标题像"和"确实聊过这个"——
    /// 后者才是用户真正想找的。
    /// </summary>
    public async Task<List<ChatSessionSummary>> SearchAsync(
        string keyword, int limit = 50, CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.Id, s.Title, s.AgentId, s.ProviderId, s.ModelUsed, s.CreatedAt, s.UpdatedAt,
                   COUNT(m.Id) AS MessageCount,
                   COALESCE(SUM(m.TotalTokens), 0) AS TotalTokens,
                   (SELECT COUNT(*) FROM Messages hit
                    WHERE hit.SessionId = s.Id AND hit.Content LIKE $q ESCAPE '\') AS MatchCount
            FROM Sessions s
            LEFT JOIN Messages m ON m.SessionId = s.Id
            WHERE s.Title LIKE $q ESCAPE '\'
               OR EXISTS (SELECT 1 FROM Messages hit2
                          WHERE hit2.SessionId = s.Id AND hit2.Content LIKE $q ESCAPE '\')
            GROUP BY s.Id
            ORDER BY s.UpdatedAt DESC
            LIMIT $limit;
            """;
        command.AddParam("$q", $"%{EscapeLike(keyword)}%");
        command.AddParam("$limit", limit);

        var result = new List<ChatSessionSummary>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new ChatSessionSummary
            {
                Id = reader.ReadString("Id"),
                Title = reader.ReadString("Title"),
                AgentId = reader.ReadNullableString("AgentId"),
                ProviderId = reader.ReadNullableString("ProviderId"),
                ModelUsed = reader.ReadNullableString("ModelUsed"),
                MessageCount = reader.ReadInt32("MessageCount"),
                TotalTokens = reader.ReadInt64("TotalTokens"),
                CreatedAt = reader.ReadDateTimeOffset("CreatedAt"),
                UpdatedAt = reader.ReadDateTimeOffset("UpdatedAt"),
                MatchCount = reader.ReadInt32("MatchCount"),
            });
        }

        return result;
    }

    /// <summary>
    /// 转义 LIKE 的通配符。用户搜 "50%" 或 "_tmp" 时若不转义，
    /// 前者会退化成"任意后缀"、后者会退化成"任意一个字符"，结果集与预期完全不符。
    /// </summary>
    private static string EscapeLike(string keyword) =>
        keyword.Replace("\\", "\\\\", StringComparison.Ordinal)
               .Replace("%", "\\%", StringComparison.Ordinal)
               .Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>读取会话及其全部消息。</summary>
    public async Task<ChatSession?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);

        ChatSession session;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM Sessions WHERE Id = $id;";
            command.AddParam("$id", id);

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            session = new ChatSession
            {
                Id = reader.ReadString("Id"),
                Title = reader.ReadString("Title"),
                ProviderId = reader.ReadNullableString("ProviderId"),
                AgentId = reader.ReadNullableString("AgentId"),
                AgentSnapshot = reader.ReadNullableString("AgentSnapshot"),
                ModelUsed = reader.ReadNullableString("ModelUsed"),
                CreatedAt = reader.ReadDateTimeOffset("CreatedAt"),
                UpdatedAt = reader.ReadDateTimeOffset("UpdatedAt"),
            };
        }

        session.Messages = await GetMessagesAsync(connection, id, ct).ConfigureAwait(false);
        return session;
    }

    /// <summary>仅读取会话元数据，不含消息。</summary>
    public async Task<ChatSession?> GetMetadataAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Sessions WHERE Id = $id;";
        command.AddParam("$id", id);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new ChatSession
        {
            Id = reader.ReadString("Id"),
            Title = reader.ReadString("Title"),
            ProviderId = reader.ReadNullableString("ProviderId"),
            AgentId = reader.ReadNullableString("AgentId"),
            AgentSnapshot = reader.ReadNullableString("AgentSnapshot"),
            ModelUsed = reader.ReadNullableString("ModelUsed"),
            CreatedAt = reader.ReadDateTimeOffset("CreatedAt"),
            UpdatedAt = reader.ReadDateTimeOffset("UpdatedAt"),
        };
    }

    /// <summary>创建或更新会话元数据（不含消息）。</summary>
    public async Task UpsertAsync(ChatSession session, CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Sessions (Id, Title, ProviderId, AgentId, AgentSnapshot, ModelUsed, CreatedAt, UpdatedAt)
            VALUES ($id, $title, $providerId, $agentId, $agentSnapshot, $modelUsed, $createdAt, $updatedAt)
            ON CONFLICT(Id) DO UPDATE SET
                Title = excluded.Title,
                ProviderId = excluded.ProviderId,
                AgentId = excluded.AgentId,
                AgentSnapshot = excluded.AgentSnapshot,
                ModelUsed = excluded.ModelUsed,
                UpdatedAt = excluded.UpdatedAt;
            """;

        command.AddParam("$id", session.Id);
        command.AddParam("$title", session.Title);
        command.AddParam("$providerId", session.ProviderId);
        command.AddParam("$agentId", session.AgentId);
        command.AddParam("$agentSnapshot", session.AgentSnapshot);
        command.AddParam("$modelUsed", session.ModelUsed);
        command.AddParam("$createdAt", session.CreatedAt);
        command.AddParam("$updatedAt", session.UpdatedAt);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>追加一条消息，自动分配会话内的排序序号，并同步刷新会话的 UpdatedAt。</summary>
    public async Task AppendMessageAsync(
        string sessionId, ChatMessage message, CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            // SortOrder 由会话内已有的最大值递增得出。仅靠 Timestamp 排序在
            // 同一毫秒内连续写入时会不稳定，故显式维护顺序。
            command.CommandText = """
                INSERT INTO Messages (
                    Id, SessionId, Role, Content, ThinkingContent, Timestamp, StartTime, EndTime, DurationMs,
                    PromptTokens, CompletionTokens, TotalTokens, EstimatedCost, ModelUsed,
                    IsInterrupted, SortOrder)
                SELECT $id, $sessionId, $role, $content, $thinkingContent, $timestamp, $startTime, $endTime, $durationMs,
                       $promptTokens, $completionTokens, $totalTokens, $estimatedCost, $modelUsed,
                       $isInterrupted, COALESCE(MAX(SortOrder), -1) + 1
                FROM Messages WHERE SessionId = $sessionId;
                """;

            command.AddParam("$id", message.Id);
            command.AddParam("$sessionId", sessionId);
            command.AddParam("$role", message.Role.ToString());
            command.AddParam("$content", message.Content);
            command.AddParam("$thinkingContent", message.ThinkingContent);
            command.AddParam("$timestamp", message.Timestamp);
            command.AddParam("$startTime", message.StartTime);
            command.AddParam("$endTime", message.EndTime);
            command.AddParam("$durationMs", message.DurationMs);
            command.AddParam("$promptTokens", message.PromptTokens);
            command.AddParam("$completionTokens", message.CompletionTokens);
            command.AddParam("$totalTokens", message.TotalTokens);
            command.AddParam("$estimatedCost", message.EstimatedCost is null ? null : (double)message.EstimatedCost.Value);
            command.AddParam("$modelUsed", message.ModelUsed);
            command.AddParam("$isInterrupted", message.IsInterrupted);

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var touch = connection.CreateCommand())
        {
            touch.Transaction = (SqliteTransaction)transaction;
            touch.CommandText = "UPDATE Sessions SET UpdatedAt = $now WHERE Id = $sessionId;";
            touch.AddParam("$now", DateTimeOffset.Now);
            touch.AddParam("$sessionId", sessionId);
            await touch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // Messages 上的外键带 ON DELETE CASCADE，且连接已开启 foreign_keys
        command.CommandText = "DELETE FROM Sessions WHERE Id = $id;";
        command.AddParam("$id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Sessions;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
    }

    private static async Task<List<ChatMessage>> GetMessagesAsync(
        SqliteConnection connection, string sessionId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Messages WHERE SessionId = $sessionId ORDER BY SortOrder;";
        command.AddParam("$sessionId", sessionId);

        var result = new List<ChatMessage>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new ChatMessage
            {
                Id = reader.ReadString("Id"),
                Role = Enum.TryParse<MessageRole>(reader.ReadString("Role"), out var role)
                    ? role
                    : MessageRole.Assistant,
                Content = reader.ReadString("Content"),
                ThinkingContent = reader.ReadNullableString("ThinkingContent"),
                Timestamp = reader.ReadDateTimeOffset("Timestamp"),
                StartTime = reader.ReadNullableDateTimeOffset("StartTime"),
                EndTime = reader.ReadNullableDateTimeOffset("EndTime"),
                DurationMs = reader.ReadNullableInt64("DurationMs"),
                PromptTokens = reader.ReadNullableInt32("PromptTokens"),
                CompletionTokens = reader.ReadNullableInt32("CompletionTokens"),
                TotalTokens = reader.ReadNullableInt32("TotalTokens"),
                EstimatedCost = reader.ReadNullableDecimal("EstimatedCost"),
                ModelUsed = reader.ReadNullableString("ModelUsed"),
                IsInterrupted = reader.ReadBool("IsInterrupted"),
            });
        }

        return result;
    }
}
