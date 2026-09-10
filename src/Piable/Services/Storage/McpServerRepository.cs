using Microsoft.Data.Sqlite;
using Piable.Models;

namespace Piable.Services.Storage;

/// <summary>MCP 服务器配置的持久化。首版仅存取，连接管理在后续阶段实现。</summary>
internal sealed class McpServerRepository
{
    private readonly PiableDatabase _database;

    public McpServerRepository(PiableDatabase database) => _database = database;

    public async Task<List<McpServerConfig>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM McpServers ORDER BY Name;";

        var result = new List<McpServerConfig>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(Map(reader));
        }

        return result;
    }

    public async Task UpsertAsync(McpServerConfig server, CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO McpServers (
                Id, Name, Transport, Command, Args, Url, Headers, Enabled, CreatedAt, UpdatedAt)
            VALUES (
                $id, $name, $transport, $command, $args, $url, $headers, $enabled, $createdAt, $updatedAt)
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name,
                Transport = excluded.Transport,
                Command = excluded.Command,
                Args = excluded.Args,
                Url = excluded.Url,
                Headers = excluded.Headers,
                Enabled = excluded.Enabled,
                UpdatedAt = excluded.UpdatedAt;
            """;

        command.AddParam("$id", server.Id);
        command.AddParam("$name", server.Name);
        command.AddParam("$transport", server.Transport.ToString());
        command.AddParam("$command", server.Command);
        command.AddParam("$args", SqliteExtensions.SerializeStringList(server.Args));
        command.AddParam("$url", server.Url);
        command.AddParam("$headers", SqliteExtensions.SerializeStringDictionary(server.Headers));
        command.AddParam("$enabled", server.Enabled);
        command.AddParam("$createdAt", server.CreatedAt);
        command.AddParam("$updatedAt", server.UpdatedAt);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM McpServers WHERE Id = $id;";
        command.AddParam("$id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static McpServerConfig Map(SqliteDataReader reader) => new()
    {
        Id = reader.ReadString("Id"),
        Name = reader.ReadString("Name"),
        Transport = Enum.TryParse<McpTransport>(reader.ReadString("Transport"), out var transport)
            ? transport
            : McpTransport.Stdio,
        Command = reader.ReadNullableString("Command"),
        Args = SqliteExtensions.DeserializeStringList(reader.ReadNullableString("Args")),
        Url = reader.ReadNullableString("Url"),
        Headers = SqliteExtensions.DeserializeStringDictionary(reader.ReadNullableString("Headers")),
        Enabled = reader.ReadBool("Enabled"),
        CreatedAt = reader.ReadDateTimeOffset("CreatedAt"),
        UpdatedAt = reader.ReadDateTimeOffset("UpdatedAt"),
    };
}
