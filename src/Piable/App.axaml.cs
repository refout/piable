using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Styling;
using LiveMarkdown.Avalonia;
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

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Markdown 扩展（Mermaid 图表）必须在任何 MarkdownRenderer 实例化之前注册一次。
        // UseMermaid 往 Markdig 管线追加 mermaid 代码块解析；Register<MermaidBlockNode>
        // 让渲染器把 ```mermaid 块映射成原生 MermaidPresenter。两者都是静态注册，重复调用无害。
        RegisterMarkdownExtensions();

        // 先把默认语言灌进资源字典：窗口比偏好从数据库里读出来要早，
        // 少了这一步，首帧的 {DynamicResource Loc.xxx} 全都是空值。
        //
        // 传 this.Resources 而不是走 Application.Current：此刻静态的 Current 尚未赋值，
        // 走默认路径会静默地什么都不做，界面于是一片空白。
        LocalizedResources.Apply(Loc.Service, Resources);
    }

    /// <summary>
    /// 注册 LiveMarkdown 的可选扩展。Mermaid 图表支持需要：
    /// 1. 往 Markdig 管线追加 UseMermaid（解析 ```mermaid 代码块）；
    /// 2. 注册 MermaidBlockNode（把该块渲染成原生 MermaidPresenter）。
    /// 必须在 MarkdownRenderer 创建前调用，故放在 Application.Initialize 里。
    /// </summary>
    private static void RegisterMarkdownExtensions()
    {
        MarkdownRenderer.ConfigurePipeline += pipeline => pipeline.UseMermaid();
        MarkdownNode.Register<MermaidBlockNode>();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = BuildServices();

            var viewModel = _services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = viewModel };

            viewModel.ThemeChangeRequested += (_, theme) => ApplyTheme(window, theme);
            viewModel.WindowBlurChangeRequested += (_, mode) => ApplyWindowBlur(window, mode);

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

        // 全进程共用同一个实例：C# 侧的 Loc.Get 走静态门面 Loc.Service，
        // 注册的是同一个对象，界面与运行时消息才不会各说一种语言。
        services.AddSingleton<ILocalizationService>(_ => Loc.Service);
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

        services.AddSingleton<IAgentResourceService>(sp => new AgentResourceService(
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
            sp.GetRequiredService<IAgentResourceService>(),
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

            // 语言要在界面读数据之前定下来：偏好里的语言未必是默认语言，
            // 而且切换会重刷资源字典，晚一步界面就先按默认语言渲染了一遍。
            await ApplyLanguage(services).ConfigureAwait(true);

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

    /// <summary>
    /// 按偏好设置决定界面语言，并让之后的每次切换都重刷资源字典。
    /// </summary>
    private static async Task ApplyLanguage(IServiceProvider services)
    {
        var localization = services.GetRequiredService<ILocalizationService>();

        // 只注册一次：App 存活期间这个委托一直有效，不必担心重复订阅
        localization.LanguageChanged += (_, _) => LocalizedResources.Apply(localization);

        var preferences = await services.GetRequiredService<IConfigService>()
            .GetPreferencesAsync().ConfigureAwait(true);

        // 与当前语言相同时 SetLanguage 会直接返回，不会触发事件，所以补一次 Apply
        localization.SetLanguage(preferences.Language);
        LocalizedResources.Apply(localization);
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

    private static void ApplyTheme(Window window, string theme)
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

        // 主题切换会让资源字典里的画刷换色，窗口底色用的是资源里的画刷（直接赋值了本地值），
        // 必须跟着重设一遍，否则关掉模糊时是上一个主题的旧色。
        ApplyWindowBlur(window, _windowBlurMode);
    }

    /// <summary>当前生效的模糊模式，供主题切换时重设窗口底色。</summary>
    private static string _windowBlurMode = "Off";

    /// <summary>
    /// 按偏好设置主窗口的原生背景模糊。要点：
    /// 1. 这里只描述"期望的层级"，操作系统按列表顺序挑第一个它支持的——
    ///    选 Mica 在不支持的系统上会自动退回 AcrylicBlur、再退回 Blur。
    /// 2. 默认的合成模式（WinUIComposition）下窗口表面始终支持逐像素透明，
    ///    因此"关闭"不能靠空 hint 实现——空 hint 只是没有系统背板，半透明的窗口底色照样透出桌面。
    ///    所以关闭时把窗口底色绑到不透明的 AppBackground，开启时绑到半透明的 WindowTint 让背板透出来。
    /// 3. 用 Bind + DynamicResourceExtension 而非直接取值：XAML 加载器会按"有效主题"解析
    ///    （含 RequestedThemeVariant=Default 时跟随系统），且主题切换时自动换色，无需在 ApplyTheme 里手动重设。
    /// </summary>
    private static void ApplyWindowBlur(Window window, string mode)
    {
        _windowBlurMode = mode;

        window.TransparencyLevelHint = mode switch
        {
            "Mica" => [WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur],
            "AcrylicBlur" => [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur],
            _ => [],
        };

        window.Bind(Avalonia.Controls.TopLevel.BackgroundProperty,
            new DynamicResourceExtension(mode == "Off" ? "AppBackground" : "WindowTint"));
    }
}
