using Microsoft.Data.Sqlite;
using Piable.Helpers;
using Piable.Models;
using Piable.Services.Storage;

namespace Piable.Tests.Storage;

public class DatabaseTests
{
    [Fact]
    public async Task 初始化后建出全部表与索引()
    {
        await using var workspace = await TestWorkspace.CreateAsync();

        var tables = await QueryNamesAsync(
            workspace, "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;");

        Assert.Contains("Providers", tables);
        Assert.Contains("Agents", tables);
        Assert.Contains("McpServers", tables);
        Assert.Contains("Skills", tables);
        Assert.Contains("Sessions", tables);
        Assert.Contains("Messages", tables);
        Assert.Contains("Preferences", tables);

        var indexes = await QueryNamesAsync(
            workspace, "SELECT name FROM sqlite_master WHERE type = 'index' ORDER BY name;");
        Assert.Contains("idx_sessions_updated", indexes);
        Assert.Contains("idx_messages_session", indexes);
    }

    [Fact]
    public async Task 重复初始化是幂等的()
    {
        await using var workspace = await TestWorkspace.CreateAsync();

        await workspace.Database.InitializeAsync();
        await workspace.Database.InitializeAsync();

        await using var connection = await workspace.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Assert.Equal((long)PiableDatabase.CurrentSchemaVersion, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task 启用WAL模式()
    {
        await using var workspace = await TestWorkspace.CreateAsync();

        await using var connection = await workspace.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";

        Assert.Equal("wal", ((string)(await command.ExecuteScalarAsync())!).ToLowerInvariant());
    }

    [Fact]
    public async Task 每个连接都开启外键约束()
    {
        await using var workspace = await TestWorkspace.CreateAsync();

        // foreign_keys 是连接级开关，漏设会让 ON DELETE CASCADE 静默失效
        await using var connection = await workspace.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys;";

        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task 备份产出可独立打开的完整快照()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.Providers.UpsertAsync(new ProviderConfig
        {
            Id = "p1",
            PresetId = ProviderPresets.OpenAi,
            DefaultModel = "gpt-4o-mini",
        });

        await workspace.Database.BackupToAsync(workspace.Paths.BackupPath);

        Assert.True(File.Exists(workspace.Paths.BackupPath));
        Assert.True(PiableDatabase.IsReadable(workspace.Paths.BackupPath));

        // 备份内容应与主库一致
        var backup = new PiableDatabase(workspace.Paths.BackupPath);
        var repository = new ProviderRepository(backup, workspace.Protector);
        Assert.Equal("p1", (await repository.GetAllAsync()).Single().Id);
    }

    [Fact]
    public async Task 重复备份会覆盖上一次的结果()
    {
        await using var workspace = await TestWorkspace.CreateAsync();

        await workspace.Database.BackupToAsync(workspace.Paths.BackupPath);
        await workspace.Providers.UpsertAsync(new ProviderConfig
        {
            Id = "p1",
            PresetId = ProviderPresets.OpenAi,
            DefaultModel = "gpt-4o-mini",
        });
        await workspace.Database.BackupToAsync(workspace.Paths.BackupPath);

        var backup = new PiableDatabase(workspace.Paths.BackupPath);
        var repository = new ProviderRepository(backup, workspace.Protector);
        Assert.Single(await repository.GetAllAsync());
    }

    [Fact]
    public void 损坏的文件不被判定为可读()
    {
        var path = Path.Combine(Path.GetTempPath(), $"piable-corrupt-{Guid.NewGuid():n}.db");
        File.WriteAllText(path, "这根本不是 SQLite 数据库文件");

        try
        {
            Assert.False(PiableDatabase.IsReadable(path));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public void 不存在的文件不被判定为可读()
    {
        Assert.False(PiableDatabase.IsReadable(
            Path.Combine(Path.GetTempPath(), $"piable-absent-{Guid.NewGuid():n}.db")));
    }

    [Fact]
    public async Task 数据库文件落在指定的数据目录下()
    {
        await using var workspace = await TestWorkspace.CreateAsync();

        Assert.True(File.Exists(workspace.Paths.DatabasePath));
        Assert.Equal(Path.Combine(workspace.Root, "piable.db"), workspace.Paths.DatabasePath);
    }

    [Theory]
    [InlineData("piable.db")]
    [InlineData("piable.backup.db")]
    [InlineData("piable.key")]
    public void 数据目录下的各个路径均由根目录派生(string fileName)
    {
        var paths = new AppPaths(Path.Combine("root", "dir"));

        var actual = fileName switch
        {
            "piable.db" => paths.DatabasePath,
            "piable.backup.db" => paths.BackupPath,
            _ => paths.KeyFilePath,
        };

        Assert.Equal(Path.Combine("root", "dir", fileName), actual);
    }

    [Fact]
    public void 默认数据目录以应用名结尾()
    {
        Assert.EndsWith("Piable", AppPaths.ResolveDefaultRoot());
    }

    private static async Task<List<string>> QueryNamesAsync(TestWorkspace workspace, string sql)
    {
        await using var connection = await workspace.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
