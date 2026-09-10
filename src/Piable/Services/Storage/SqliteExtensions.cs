using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Piable.Models;

namespace Piable.Services.Storage;

/// <summary>
/// SQLite 读写的基础类型转换。
///
/// 时间统一以 UTC 的 ISO-8601 往返格式（"O"）存储：既是字符串也是可排序的，
/// 且避免本地时区与夏令时带来的歧义；显示时再由界面转回本地时间。
/// </summary>
internal static class SqliteExtensions
{
    private const string TimestampFormat = "O";

    public static string ToDbString(this DateTimeOffset value) =>
        value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    public static string? ToDbString(this DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    public static DateTimeOffset ToDateTimeOffset(this string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static DateTimeOffset? ToDateTimeOffsetOrNull(this string? value) =>
        string.IsNullOrEmpty(value) ? null : value.ToDateTimeOffset();

    // ---- reader 辅助：统一处理 DBNull ----

    public static string ReadString(this SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
    }

    public static string? ReadNullableString(this SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    public static DateTimeOffset ReadDateTimeOffset(this SqliteDataReader reader, string column) =>
        reader.ReadString(column).ToDateTimeOffset();

    public static DateTimeOffset? ReadNullableDateTimeOffset(this SqliteDataReader reader, string column) =>
        reader.ReadNullableString(column).ToDateTimeOffsetOrNull();

    public static int ReadInt32(this SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? 0 : reader.GetInt32(ordinal);
    }

    public static int? ReadNullableInt32(this SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    public static long ReadInt64(this SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? 0L : reader.GetInt64(ordinal);
    }

    public static long? ReadNullableInt64(this SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    public static double ReadDouble(this SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? 0d : reader.GetDouble(ordinal);
    }

    public static double? ReadNullableDouble(this SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    }

    /// <summary>SQLite 无 decimal 类型，金额以 REAL 存取；此处做一次显式转换。</summary>
    public static decimal ReadDecimal(this SqliteDataReader reader, string column) =>
        (decimal)reader.ReadDouble(column);

    public static decimal? ReadNullableDecimal(this SqliteDataReader reader, string column)
    {
        var value = reader.ReadNullableDouble(column);
        return value is null ? null : (decimal)value.Value;
    }

    /// <summary>SQLite 以 INTEGER 0/1 表示布尔。</summary>
    public static bool ReadBool(this SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return !reader.IsDBNull(ordinal) && reader.GetInt64(ordinal) != 0;
    }

    // ---- 参数辅助 ----

    /// <summary>添加参数，把 null 映射为 DBNull。</summary>
    public static void AddParam(this SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    public static void AddParam(this SqliteCommand command, string name, bool value) =>
        command.Parameters.AddWithValue(name, value ? 1 : 0);

    public static void AddParam(this SqliteCommand command, string name, DateTimeOffset value) =>
        command.Parameters.AddWithValue(name, value.ToDbString());

    public static void AddParam(this SqliteCommand command, string name, DateTimeOffset? value) =>
        command.Parameters.AddWithValue(name, (object?)value.ToDbString() ?? DBNull.Value);

    // ---- JSON 列的序列化 ----

    public static string SerializeStringList(IEnumerable<string> values) =>
        JsonSerializer.Serialize(values.ToList(), PiableJsonContext.Default.StringList);

    public static List<string> DeserializeStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(json, PiableJsonContext.Default.StringList) ?? [];
        }
        catch (JsonException)
        {
            // 数据损坏时退化为空列表，不因单行脏数据导致整个列表加载失败
            return [];
        }
    }

    public static string SerializeStringDictionary(IDictionary<string, string> values) =>
        JsonSerializer.Serialize(
            new Dictionary<string, string>(values, StringComparer.Ordinal),
            PiableJsonContext.Default.StringDictionary);

    public static Dictionary<string, string> DeserializeStringDictionary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(json, PiableJsonContext.Default.StringDictionary) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
