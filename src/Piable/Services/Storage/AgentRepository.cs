using Microsoft.Data.Sqlite;
using Piable.Models;

namespace Piable.Services.Storage;

/// <summary>智能体的持久化。</summary>
internal sealed class AgentRepository
{
    private readonly PiableDatabase _database;

    public AgentRepository(PiableDatabase database) => _database = database;

    public async Task<List<Agent>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Agents ORDER BY IsDefault DESC, IsBuiltIn DESC, Name;";

        var result = new List<Agent>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(Map(reader));
        }

        return result;
    }

    public async Task<Agent?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Agents WHERE Id = $id;";
        command.AddParam("$id", id);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task UpsertAsync(Agent agent, CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        if (agent.IsDefault)
        {
            await using var clear = connection.CreateCommand();
            clear.Transaction = (SqliteTransaction)transaction;
            clear.CommandText = "UPDATE Agents SET IsDefault = 0 WHERE Id <> $id;";
            clear.AddParam("$id", agent.Id);
            await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO Agents (
                Id, Name, Description, SystemPrompt, Model, Temperature, MaxTokens, TopP,
                McpServerIds, SkillIds, IsDefault, IsBuiltIn, CreatedAt, UpdatedAt)
            VALUES (
                $id, $name, $description, $systemPrompt, $model, $temperature, $maxTokens, $topP,
                $mcpServerIds, $skillIds, $isDefault, $isBuiltIn, $createdAt, $updatedAt)
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name,
                Description = excluded.Description,
                SystemPrompt = excluded.SystemPrompt,
                Model = excluded.Model,
                Temperature = excluded.Temperature,
                MaxTokens = excluded.MaxTokens,
                TopP = excluded.TopP,
                McpServerIds = excluded.McpServerIds,
                SkillIds = excluded.SkillIds,
                IsDefault = excluded.IsDefault,
                UpdatedAt = excluded.UpdatedAt;
            """;

        command.AddParam("$id", agent.Id);
        command.AddParam("$name", agent.Name);
        command.AddParam("$description", agent.Description);
        command.AddParam("$systemPrompt", agent.SystemPrompt);
        command.AddParam("$model", agent.Model);
        command.AddParam("$temperature", agent.Temperature);
        command.AddParam("$maxTokens", agent.MaxTokens);
        command.AddParam("$topP", agent.TopP);
        command.AddParam("$mcpServerIds", SqliteExtensions.SerializeStringList(agent.McpServerIds));
        command.AddParam("$skillIds", SqliteExtensions.SerializeStringList(agent.SkillIds));
        command.AddParam("$isDefault", agent.IsDefault);
        command.AddParam("$isBuiltIn", agent.IsBuiltIn);
        command.AddParam("$createdAt", agent.CreatedAt);
        command.AddParam("$updatedAt", agent.UpdatedAt);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // 内置智能体不允许删除；这里再做一次兜底，防止绕过界面直接调用
        command.CommandText = "DELETE FROM Agents WHERE Id = $id AND IsBuiltIn = 0;";
        command.AddParam("$id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>统计引用了该智能体的会话数量。</summary>
    public async Task<int> CountReferencingSessionsAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Sessions WHERE AgentId = $id;";
        command.AddParam("$id", id);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
    }

    private static Agent Map(SqliteDataReader reader) => new()
    {
        Id = reader.ReadString("Id"),
        Name = reader.ReadString("Name"),
        Description = reader.ReadNullableString("Description"),
        SystemPrompt = reader.ReadString("SystemPrompt"),
        Model = reader.ReadNullableString("Model"),
        Temperature = reader.ReadNullableDouble("Temperature"),
        MaxTokens = reader.ReadNullableInt32("MaxTokens"),
        TopP = reader.ReadNullableDouble("TopP"),
        McpServerIds = SqliteExtensions.DeserializeStringList(reader.ReadNullableString("McpServerIds")),
        SkillIds = SqliteExtensions.DeserializeStringList(reader.ReadNullableString("SkillIds")),
        IsDefault = reader.ReadBool("IsDefault"),
        IsBuiltIn = reader.ReadBool("IsBuiltIn"),
        CreatedAt = reader.ReadDateTimeOffset("CreatedAt"),
        UpdatedAt = reader.ReadDateTimeOffset("UpdatedAt"),
    };
}
