using System.Text.Json;
using Piable.Models;

namespace Piable.Services.Storage;

/// <summary>
/// 用户偏好的持久化。
/// 整体以单条 JSON 记录存放：偏好项会随版本增删，逐项建列或逐键存行都会带来迁移负担，
/// 而整块 JSON 对缺失字段天然向后兼容（反序列化时回落到属性默认值）。
/// </summary>
public sealed class PreferenceRepository
{
    private const string PreferencesKey = "user_preferences";

    private readonly PiableDatabase _database;

    public PreferenceRepository(PiableDatabase database) => _database = database;

    /// <summary>读取偏好；从未保存过或数据损坏时返回默认值。</summary>
    public async Task<UserPreferences> LoadAsync(CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Preferences WHERE Key = $key;";
        command.AddParam("$key", PreferencesKey);

        var json = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (string.IsNullOrWhiteSpace(json))
        {
            return new UserPreferences();
        }

        try
        {
            return JsonSerializer.Deserialize(json, PiableJsonContext.Default.UserPreferences)
                   ?? new UserPreferences();
        }
        catch (JsonException)
        {
            // 偏好损坏不应阻断启动，退回默认值即可
            return new UserPreferences();
        }
    }

    public async Task SaveAsync(UserPreferences preferences, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(preferences, PiableJsonContext.Default.UserPreferences);

        await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Preferences (Key, Value, UpdatedAt)
            VALUES ($key, $value, $updatedAt)
            ON CONFLICT(Key) DO UPDATE SET
                Value = excluded.Value,
                UpdatedAt = excluded.UpdatedAt;
            """;

        command.AddParam("$key", PreferencesKey);
        command.AddParam("$value", json);
        command.AddParam("$updatedAt", DateTimeOffset.Now);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
