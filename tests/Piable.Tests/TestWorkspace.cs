using Piable.Helpers;
using Piable.Services.Storage;

namespace Piable.Tests;

/// <summary>
/// 一套指向临时目录的完整存储栈，供测试使用。
/// 每个实例独占一个临时目录，测试之间互不影响，也不会碰到真实用户数据。
/// </summary>
internal sealed class TestWorkspace : IAsyncDisposable
{
    private TestWorkspace(string root, AppPaths paths, PiableDatabase database)
    {
        Root = root;
        Paths = paths;
        Database = database;
        Protector = AesGcmSecretProtector.LoadOrCreate(paths.KeyFilePath);
        Providers = new ProviderRepository(database, Protector);
        Agents = new AgentRepository(database);
        McpServers = new McpServerRepository(database);
        Skills = new SkillRepository(database);
        Sessions = new SessionRepository(database);
        Preferences = new PreferenceRepository(database);
    }

    public string Root { get; }
    public AppPaths Paths { get; }
    public PiableDatabase Database { get; }
    public ISecretProtector Protector { get; }
    public ProviderRepository Providers { get; }
    public AgentRepository Agents { get; }
    public McpServerRepository McpServers { get; }
    public SkillRepository Skills { get; }
    public SessionRepository Sessions { get; }
    public PreferenceRepository Preferences { get; }

    public static async Task<TestWorkspace> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "piable-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        var paths = new AppPaths(root);
        var database = new PiableDatabase(paths.DatabasePath);
        await database.InitializeAsync();

        return new TestWorkspace(root, paths, database);
    }

    public ValueTask DisposeAsync()
    {
        // Microsoft.Data.Sqlite 会池化连接，不清空池就删不掉文件
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清理失败不应让测试失败
        }

        return ValueTask.CompletedTask;
    }
}
