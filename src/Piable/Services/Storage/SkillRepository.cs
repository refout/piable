using Microsoft.Data.Sqlite;
using Piable.Models;

namespace Piable.Services.Storage;

/// <summary>技能定义的持久化。首版仅存取，执行在后续阶段实现。</summary>
public sealed class SkillRepository
{
    private readonly PiableDatabase _database;

    public SkillRepository(PiableDatabase database) => _database = database;

    public async Task<List<SkillDefinition>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Skills ORDER BY Name;";

        var result = new List<SkillDefinition>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(Map(reader));
        }

        return result;
    }

    public async Task UpsertAsync(SkillDefinition skill, CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Skills (
                Id, Name, Description, ToolSpec, Handler, Enabled, CreatedAt, UpdatedAt)
            VALUES (
                $id, $name, $description, $toolSpec, $handler, $enabled, $createdAt, $updatedAt)
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name,
                Description = excluded.Description,
                ToolSpec = excluded.ToolSpec,
                Handler = excluded.Handler,
                Enabled = excluded.Enabled,
                UpdatedAt = excluded.UpdatedAt;
            """;

        command.AddParam("$id", skill.Id);
        command.AddParam("$name", skill.Name);
        command.AddParam("$description", skill.Description);
        command.AddParam("$toolSpec", skill.ToolSpec);
        command.AddParam("$handler", skill.Handler);
        command.AddParam("$enabled", skill.Enabled);
        command.AddParam("$createdAt", skill.CreatedAt);
        command.AddParam("$updatedAt", skill.UpdatedAt);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Skills WHERE Id = $id;";
        command.AddParam("$id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static SkillDefinition Map(SqliteDataReader reader) => new()
    {
        Id = reader.ReadString("Id"),
        Name = reader.ReadString("Name"),
        Description = reader.ReadString("Description"),
        ToolSpec = reader.ReadString("ToolSpec"),
        Handler = reader.ReadString("Handler"),
        Enabled = reader.ReadBool("Enabled"),
        CreatedAt = reader.ReadDateTimeOffset("CreatedAt"),
        UpdatedAt = reader.ReadDateTimeOffset("UpdatedAt"),
    };
}
