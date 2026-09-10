namespace Piable.Helpers;

/// <summary>
/// 应用数据目录的路径解析。
/// 以实例形式提供（而非静态），测试可传入临时目录，避免污染真实用户数据。
/// </summary>
public sealed class AppPaths
{
    private const string AppFolderName = "Piable";

    public AppPaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        Root = rootDirectory;
    }

    /// <summary>应用数据根目录。</summary>
    public string Root { get; }

    /// <summary>主数据库文件。</summary>
    public string DatabasePath => Path.Combine(Root, "piable.db");

    /// <summary>退出时生成的备份数据库。</summary>
    public string BackupPath => Path.Combine(Root, "piable.backup.db");

    /// <summary>API Key 加密所用密钥文件。</summary>
    public string KeyFilePath => Path.Combine(Root, "piable.key");

    /// <summary>运行日志文件。</summary>
    public string LogPath => Path.Combine(Root, "piable.log");

    /// <summary>按当前平台解析默认数据目录，并确保其存在。</summary>
    public static AppPaths CreateDefault()
    {
        var root = ResolveDefaultRoot();
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        return paths;
    }

    /// <summary>
    /// 各平台的数据目录：
    /// Windows 为 %AppData%\Piable，macOS 为 ~/Library/Application Support/Piable，
    /// Linux 为 ~/.config/Piable。
    /// </summary>
    internal static string ResolveDefaultRoot()
    {
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", AppFolderName);
        }

        // Windows 的 ApplicationData 即 %AppData%（漫游）；Linux 上为 ~/.config
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(baseDir, AppFolderName);
    }

    /// <summary>创建数据目录。已存在时为空操作。</summary>
    public void EnsureCreated() => Directory.CreateDirectory(Root);
}
