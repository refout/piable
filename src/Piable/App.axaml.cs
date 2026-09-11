using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Piable.Helpers;
using Piable.Services;
using Piable.Services.Storage;
using Piable.Services.Tools;
using Piable.ViewModels;
using Piable.Views;

namespace Piable;

public partial class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = BuildServices();

            var viewModel = _services.GetRequiredService<MainWindowViewModel>();
            viewModel.ThemeChangeRequested += (_, theme) => ApplyTheme(theme);

            var window = new MainWindow { DataContext = viewModel };

            // 尺寸必须在窗口显示之后再定：窗口尚未创建平台句柄时 Screens 为 null，
            // 那时算不出可用区域，只能退回 XAML 里写死的 1100×720 ——
            // 在高 DPI 的小屏上这会得到一个比屏幕还大的窗口，右边缘被推到屏幕外。
            window.Opened += (_, _) => ApplyInitialSize(window);

            desktop.MainWindow = window;
            desktop.ShutdownRequested += OnShutdownRequested;

            _ = StartAsync(_services);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 全部依赖用显式工厂委托注册。
    /// 不用 <c>AddSingleton&lt;IFoo, Foo&gt;()</c> 的自动激活：那条路走反射构造，
    /// 与项目追求的 AOT 兼容性冲突，而工厂委托是编译期就确定的。
    /// </summary>
    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        var paths = AppPaths.CreateDefault();
        services.AddSingleton(paths);
        services.AddSingleton<ISecretProtector>(_ => AesGcmSecretProtector.LoadOrCreate(paths.KeyFilePath));
        services.AddSingleton(_ => new PiableDatabase(paths.DatabasePath));

        services.AddSingleton(sp => new ProviderRepository(
            sp.GetRequiredService<PiableDatabase>(), sp.GetRequiredService<ISecretProtector>()));
        services.AddSingleton(sp => new AgentRepository(sp.GetRequiredService<PiableDatabase>()));
        services.AddSingleton(sp => new McpServerRepository(sp.GetRequiredService<PiableDatabase>()));
        services.AddSingleton(sp => new SkillRepository(sp.GetRequiredService<PiableDatabase>()));
        services.AddSingleton(sp => new SessionRepository(sp.GetRequiredService<PiableDatabase>()));
        services.AddSingleton(sp => new PreferenceRepository(sp.GetRequiredService<PiableDatabase>()));

        services.AddSingleton<IConfigService>(sp => new ConfigService(
            sp.GetRequiredService<ProviderRepository>(),
            sp.GetRequiredService<AgentRepository>(),
            sp.GetRequiredService<PreferenceRepository>(),
            sp.GetRequiredService<McpServerRepository>()));

        services.AddSingleton<ISkillService>(sp => new SkillService(
            sp.GetRequiredService<SkillRepository>()));

        // MCP 连接持有子进程与网络会话，必须是单例，否则每处解析都会新开一份连接
        services.AddSingleton<IMcpClientService>(_ => new McpClientService());

        services.AddSingleton<IToolCatalog>(sp => new ToolCatalog(
            sp.GetRequiredService<ISkillService>(),
            sp.GetRequiredService<IMcpClientService>(),
            sp.GetRequiredService<IConfigService>()));

        services.AddSingleton<ISessionService>(sp => new SessionService(
            sp.GetRequiredService<SessionRepository>()));

        services.AddSingleton<ITokenCostCalculator>(_ => new TokenCostCalculator());
        services.AddSingleton<IChatClientFactory>(_ => new ChatClientFactory());

        // 只给"获取模型列表"用；对话走 OpenAI SDK 自带的 HttpClient。
        // 超时给 30 秒：拉列表是个短请求，不该无限等待。
        services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
        services.AddSingleton<IModelListService>(sp => new ModelListService(
            sp.GetRequiredService<HttpClient>()));

        services.AddSingleton<IAgentOrchestrator>(sp => new AgentOrchestrator(
            sp.GetRequiredService<IChatClientFactory>()));

        services.AddSingleton(sp => new MainWindowViewModel(
            sp.GetRequiredService<IConfigService>(),
            sp.GetRequiredService<ISessionService>(),
            sp.GetRequiredService<IAgentOrchestrator>(),
            sp.GetRequiredService<ITokenCostCalculator>(),
            sp.GetRequiredService<IModelListService>(),
            sp.GetRequiredService<ISkillService>(),
            sp.GetRequiredService<IToolCatalog>(),
            sp.GetRequiredService<IMcpClientService>()));

        return services.BuildServiceProvider();
    }

    private static async Task StartAsync(IServiceProvider services)
    {
        try
        {
            var paths = services.GetRequiredService<AppPaths>();
            var database = services.GetRequiredService<PiableDatabase>();

            // 主库打不开时先用备份顶上，再建表
            await database.RestoreFromBackupIfNeededAsync(paths.BackupPath).ConfigureAwait(true);
            await database.InitializeAsync().ConfigureAwait(true);

            // 写入内置技能，须在界面读取工具之前完成
            await services.GetRequiredService<ISkillService>().InitializeAsync().ConfigureAwait(true);

            await services.GetRequiredService<MainWindowViewModel>().InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 启动阶段的异常无处上报，写到日志文件里便于排查
            TryWriteStartupLog(services, ex);
        }
    }

    private static void TryWriteStartupLog(IServiceProvider services, Exception exception)
    {
        try
        {
            var paths = services.GetRequiredService<AppPaths>();
            File.AppendAllText(
                paths.LogPath,
                $"[{DateTimeOffset.Now:O}] 启动失败：{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 日志都写不进去就只能放弃了
        }
    }

    /// <summary>退出前备份数据库（设计文档 6.5）。</summary>
    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (_services is null)
        {
            return;
        }

        try
        {
            // MCP 连接持有子进程，必须显式关闭，否则会留下孤儿进程
            _services.GetRequiredService<IMcpClientService>()
                .DisposeAsync().AsTask().GetAwaiter().GetResult();

            var paths = _services.GetRequiredService<AppPaths>();
            _services.GetRequiredService<PiableDatabase>().BackupTo(paths.BackupPath);
        }
        catch (Exception ex)
        {
            TryWriteStartupLog(_services, ex);
        }
    }

    /// <summary>
    /// 按屏幕工作区决定初始窗口尺寸。
    /// 不能只看 XAML 里写死的 1100×720：那是逻辑像素，在 200% 缩放的屏幕上
    /// 会变成 2200×1440 物理像素，直接超出屏幕、把右侧和底部挤到可视区域之外。
    /// </summary>
    private static void ApplyInitialSize(Window window)
    {
        const double preferredWidth = 1100;
        const double preferredHeight = 720;
        const double margin = 40;

        var screen = window.Screens.ScreenFromWindow(window)
                     ?? window.Screens.Primary
                     ?? window.Screens.All.FirstOrDefault();

        if (screen is null)
        {
            return;
        }

        var scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        var availableWidth = screen.WorkingArea.Width / scaling - margin;
        var availableHeight = screen.WorkingArea.Height / scaling - margin;

        // 收窄到可用区域，但不小于窗口声明的最小尺寸——低于 MinWidth 布局会撑不住。
        // 若屏幕本身比 MinWidth 还窄，窗口仍然会溢出屏幕；
        // 那种情况下宁可溢出，也好过把窗口压到连最小可用布局都放不下的尺寸。
        window.Width = Math.Max(window.MinWidth, Math.Min(preferredWidth, availableWidth));
        window.Height = Math.Max(window.MinHeight, Math.Min(preferredHeight, availableHeight));

        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    private static void ApplyTheme(string theme)
    {
        if (Current is null)
        {
            return;
        }

        Current.RequestedThemeVariant = theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
