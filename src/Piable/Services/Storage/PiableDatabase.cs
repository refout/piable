using Microsoft.Data.Sqlite;

namespace Piable.Services.Storage;

/// <summary>
/// SQLite 连接与结构管理。
/// 采用"每次操作开一个连接、用完即还"的方式，由 Microsoft.Data.Sqlite 的连接池承担复用开销，
/// 避免长时间持有连接带来的并发写冲突。
/// </summary>
public sealed class PiableDatabase
{
    /// <summary>当前结构版本，写入 SQLite 的 user_version 字段。</summary>
    public const int CurrentSchemaVersion = 1;

    private readonly string _connectionString;

    public PiableDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        DatabasePath = databasePath;

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    public string DatabasePath { get; }

    /// <summary>打开一个已配置好的连接。调用方负责释放。</summary>
    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // foreign_keys 是连接级开关，必须每连接设置，否则 ON DELETE CASCADE 不会生效
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    /// <summary>异步打开连接。</summary>
    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return connection;
    }

    /// <summary>建库并升级到当前结构版本。可重复调用。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);

        // WAL 提升并发读性能，且该设置持久化在数据库文件中，只需设置一次
        await ExecuteScalarAsync(connection, "PRAGMA journal_mode = WAL;", ct).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "PRAGMA synchronous = NORMAL;", ct).ConfigureAwait(false);

        var version = Convert.ToInt32(
            await ExecuteScalarAsync(connection, "PRAGMA user_version;", ct).ConfigureAwait(false) ?? 0);

        if (version < 1)
        {
            await ExecuteNonQueryAsync(connection, SchemaV1, ct).ConfigureAwait(false);
        }

        if (version != CurrentSchemaVersion)
        {
            await ExecuteNonQueryAsync(
                connection, $"PRAGMA user_version = {CurrentSchemaVersion};", ct).ConfigureAwait(false);
        }
    }

    /// <summary>把数据库整体备份到指定路径（使用 VACUUM INTO，产出一致性快照）。</summary>
    public async Task BackupToAsync(string destinationPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);

        // VACUUM INTO 要求目标文件不存在；先删掉上一次的备份
        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        await using var command = connection.CreateCommand();
        // 路径不能参数化，只能拼接；用单引号转义防止路径中的引号破坏语句
        command.CommandText = $"VACUUM INTO '{destinationPath.Replace("'", "''")}';";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>同步版本的备份，供应用退出时调用（退出流程不适合 await）。</summary>
    public void BackupTo(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        using var connection = OpenConnection();

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"VACUUM INTO '{destinationPath.Replace("'", "''")}';";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 启动自愈（设计文档 6.5）：主库能正常打开就什么都不做；
    /// 主库缺失或损坏而备份可用时，用备份顶替，避免用户直接丢失全部历史。
    /// </summary>
    /// <returns>是否执行了从备份恢复。</returns>
    public async Task<bool> RestoreFromBackupIfNeededAsync(
        string backupPath, CancellationToken ct = default)
    {
        if (IsReadable(DatabasePath))
        {
            return false;
        }

        if (!IsReadable(backupPath))
        {
            // 没有可用备份：交给 InitializeAsync 建一个新库
            return false;
        }

        SqliteConnection.ClearAllPools();

        // 损坏的主库先挪到一边而不是直接删掉，万一还有人工抢救的余地
        if (File.Exists(DatabasePath))
        {
            var salvagePath = DatabasePath + ".corrupt";
            File.Move(DatabasePath, salvagePath, overwrite: true);
        }

        File.Copy(backupPath, DatabasePath);
        await InitializeAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>检查数据库文件是否可正常打开（用于启动时的备份完整性校验）。</summary>
    public static bool IsReadable(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return false;
        }

        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
            };

            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            return string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<object?> ExecuteScalarAsync(
        SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    /// <summary>结构版本 1：对应设计文档 6.3 的表结构。</summary>
    private const string SchemaV1 = """
        CREATE TABLE IF NOT EXISTS Providers (
            Id TEXT PRIMARY KEY,
            PresetId TEXT NOT NULL,
            ApiKey TEXT,
            Endpoint TEXT,
            DeploymentName TEXT,
            Models TEXT NOT NULL,
            DefaultModel TEXT NOT NULL,
            Temperature REAL,
            MaxTokens INTEGER,
            TopP REAL,
            InputPricePer1K REAL NOT NULL,
            OutputPricePer1K REAL NOT NULL,
            IsDefault INTEGER NOT NULL,
            ModelListUpdatedAt TEXT,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS Agents (
            Id TEXT PRIMARY KEY,
            Name TEXT NOT NULL,
            Description TEXT,
            SystemPrompt TEXT NOT NULL,
            Model TEXT,
            Temperature REAL,
            MaxTokens INTEGER,
            TopP REAL,
            McpServerIds TEXT NOT NULL,
            SkillIds TEXT NOT NULL,
            IsDefault INTEGER NOT NULL,
            IsBuiltIn INTEGER NOT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS McpServers (
            Id TEXT PRIMARY KEY,
            Name TEXT NOT NULL,
            Transport TEXT NOT NULL,
            Command TEXT,
            Args TEXT NOT NULL,
            Url TEXT,
            Headers TEXT NOT NULL,
            Enabled INTEGER NOT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS Skills (
            Id TEXT PRIMARY KEY,
            Name TEXT NOT NULL,
            Description TEXT NOT NULL,
            ToolSpec TEXT NOT NULL,
            Handler TEXT NOT NULL,
            Enabled INTEGER NOT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );

        -- ProviderId / AgentId 可为空并带 ON DELETE SET NULL：
        -- 删除供应商或智能体时，用户的历史对话必须完整保留（会话自身已存有
        -- ModelUsed 与 AgentSnapshot，足以还原当时的情形），只是失去"续聊时该用谁"的指向。
        -- 若照搬设计文档的 NOT NULL，一旦有会话引用过某供应商，该供应商就再也删不掉。
        CREATE TABLE IF NOT EXISTS Sessions (
            Id TEXT PRIMARY KEY,
            Title TEXT NOT NULL,
            ProviderId TEXT,
            AgentId TEXT,
            AgentSnapshot TEXT,
            ModelUsed TEXT,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL,
            FOREIGN KEY (ProviderId) REFERENCES Providers(Id) ON DELETE SET NULL,
            FOREIGN KEY (AgentId) REFERENCES Agents(Id) ON DELETE SET NULL
        );

        CREATE TABLE IF NOT EXISTS Messages (
            Id TEXT PRIMARY KEY,
            SessionId TEXT NOT NULL,
            Role TEXT NOT NULL,
            Content TEXT NOT NULL,
            Timestamp TEXT NOT NULL,
            StartTime TEXT,
            EndTime TEXT,
            DurationMs INTEGER,
            PromptTokens INTEGER,
            CompletionTokens INTEGER,
            TotalTokens INTEGER,
            EstimatedCost REAL,
            ModelUsed TEXT,
            IsInterrupted INTEGER NOT NULL,
            SortOrder INTEGER NOT NULL,
            FOREIGN KEY (SessionId) REFERENCES Sessions(Id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS Preferences (
            Key TEXT PRIMARY KEY,
            Value TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_sessions_updated ON Sessions(UpdatedAt DESC);
        CREATE INDEX IF NOT EXISTS idx_messages_session ON Messages(SessionId, SortOrder);
        """;
}
