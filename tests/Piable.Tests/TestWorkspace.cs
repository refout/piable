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
        // 关闭连接池：池化连接会一直占着文件句柄，只能靠进程级的 ClearAllPools 释放，
        // 而那会连带清掉并行运行的其他测试正在使用的连接，导致随机失败。
        var database = new PiableDatabase(paths.DatabasePath, pooling: false);
        await database.InitializeAsync();

        return new TestWorkspace(root, paths, database);
    }

    public ValueTask DisposeAsync()
    {
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
