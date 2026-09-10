using Avalonia;
using Avalonia.Headless;
using Piable;

[assembly: AvaloniaTestApplication(typeof(Piable.Tests.Ui.TestAppBuilder))]

namespace Piable.Tests.Ui;

/// <summary>
/// 无头 UI 测试的宿主。
///
/// 用真实的 <see cref="App"/> 而不是裸的 Application，这样 App.axaml 里的
/// 样式与主题资源会被加载，布局结果才和实际运行时一致
/// （否则 DynamicResource 全部落空，测出来的排版没有参考价值）。
///
/// DI 与数据库初始化不会触发：那部分在 OnFrameworkInitializationCompleted 里
/// 由 IClassicDesktopStyleApplicationLifetime 分支保护，无头测试用的是另一种生命周期。
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
        .WithInterFont();
}
