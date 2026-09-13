using Microsoft.Data.Sqlite;
using Piable.Helpers;
using Piable.Models;

namespace Piable.Services.Storage;

/// <summary>供应商配置的持久化。API Key 在写库前加密、读出后解密。</summary>
public sealed class ProviderRepository
{
    private readonly PiableDatabase _database;
    private readonly ISecretProtector _protector;

    public ProviderRepository(PiableDatabase database, ISecretProtector protector)
    {
        _database = database;
        _protector = protector;
    }

    public async Task<List<ProviderConfig>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Providers ORDER BY IsDefault DESC, CreatedAt;";

        var result = new List<ProviderConfig>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(Map(reader));
        }

        return result;
    }

    public async Task<ProviderConfig?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Providers WHERE Id = $id;";
        command.AddParam("$id", id);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <summary>插入或更新一条供应商配置。</summary>
    public async Task UpsertAsync(ProviderConfig provider, CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        if (provider.IsDefault)
        {
            // 全局只能有一个默认供应商，先清空其余
            await using var clear = connection.CreateCommand();
            clear.Transaction = (SqliteTransaction)transaction;
            clear.CommandText = "UPDATE Providers SET IsDefault = 0 WHERE Id <> $id;";
            clear.AddParam("$id", provider.Id);
            await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO Providers (
                Id, PresetId, Name, ApiKey, Endpoint, DeploymentName, Models, DefaultModel,
                Temperature, MaxTokens, TopP, InputPricePer1K, OutputPricePer1K,
                IsDefault, ModelListUpdatedAt, CreatedAt, UpdatedAt)
            VALUES (
                $id, $presetId, $name, $apiKey, $endpoint, $deploymentName, $models, $defaultModel,
                $temperature, $maxTokens, $topP, $inputPrice, $outputPrice,
                $isDefault, $modelListUpdatedAt, $createdAt, $updatedAt)
            ON CONFLICT(Id) DO UPDATE SET
                PresetId = excluded.PresetId,
                Name = excluded.Name,
                ApiKey = excluded.ApiKey,
                Endpoint = excluded.Endpoint,
                DeploymentName = excluded.DeploymentName,
                Models = excluded.Models,
                DefaultModel = excluded.DefaultModel,
                Temperature = excluded.Temperature,
                MaxTokens = excluded.MaxTokens,
                TopP = excluded.TopP,
                InputPricePer1K = excluded.InputPricePer1K,
                OutputPricePer1K = excluded.OutputPricePer1K,
                IsDefault = excluded.IsDefault,
                ModelListUpdatedAt = excluded.ModelListUpdatedAt,
                UpdatedAt = excluded.UpdatedAt;
            """;

        command.AddParam("$id", provider.Id);
        command.AddParam("$presetId", provider.PresetId);
        command.AddParam("$name", provider.Name);
        // 加密后才落库；Ollama 等无 Key 的供应商存 NULL
        command.AddParam("$apiKey", _protector.Protect(provider.ApiKey));
        command.AddParam("$endpoint", provider.Endpoint);
        command.AddParam("$deploymentName", provider.DeploymentName);
        command.AddParam("$models", SqliteExtensions.SerializeStringList(provider.Models));
        command.AddParam("$defaultModel", provider.DefaultModel);
        command.AddParam("$temperature", provider.Temperature);
        command.AddParam("$maxTokens", provider.MaxTokens);
        command.AddParam("$topP", provider.TopP);
        command.AddParam("$inputPrice", (double)provider.InputPricePer1K);
        command.AddParam("$outputPrice", (double)provider.OutputPricePer1K);
        command.AddParam("$isDefault", provider.IsDefault);
        command.AddParam("$modelListUpdatedAt", provider.ModelListUpdatedAt);
        command.AddParam("$createdAt", provider.CreatedAt);
        command.AddParam("$updatedAt", provider.UpdatedAt);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Providers WHERE Id = $id;";
        command.AddParam("$id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>统计引用了该供应商的会话数量，用于删除前的提示。</summary>
    public async Task<int> CountReferencingSessionsAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Sessions WHERE ProviderId = $id;";
        command.AddParam("$id", id);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
    }

    private ProviderConfig Map(SqliteDataReader reader)
    {
        var storedKey = reader.ReadNullableString("ApiKey");
        return new ProviderConfig
        {
            Id = reader.ReadString("Id"),
            PresetId = reader.ReadString("PresetId"),
            Name = reader.ReadString("Name"),
            // 解密失败（例如密钥文件被删）不抛异常，退化为空 Key，让用户在界面上重新填写
            ApiKey = _protector.TryUnprotect(storedKey),
            Endpoint = reader.ReadNullableString("Endpoint"),
            DeploymentName = reader.ReadNullableString("DeploymentName"),
            Models = SqliteExtensions.DeserializeStringList(reader.ReadNullableString("Models")),
            DefaultModel = reader.ReadString("DefaultModel"),
            Temperature = reader.ReadNullableDouble("Temperature"),
            MaxTokens = reader.ReadNullableInt32("MaxTokens"),
            TopP = reader.ReadNullableDouble("TopP"),
            InputPricePer1K = reader.ReadDecimal("InputPricePer1K"),
            OutputPricePer1K = reader.ReadDecimal("OutputPricePer1K"),
            IsDefault = reader.ReadBool("IsDefault"),
            ModelListUpdatedAt = reader.ReadNullableDateTimeOffset("ModelListUpdatedAt"),
            CreatedAt = reader.ReadDateTimeOffset("CreatedAt"),
            UpdatedAt = reader.ReadDateTimeOffset("UpdatedAt"),
        };
    }
}
