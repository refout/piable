using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentIcons.Avalonia;
using Piable.Helpers;
using Piable.Services;
using Piable.ViewModels;
using Piable.Views;

namespace Piable.Tests.Ui;

/// <summary>
/// 切语言"立即生效"这件事只能靠真实布局验证：
/// 资源字典换了以后，界面上的 {DynamicResource} 必须自己重新求值，
/// 而不是等下一次重建控件——这一点单元测试测不出来。
/// </summary>
public class LocalizationUiTests
{
    private const double WindowWidth = 1100;
    private const double WindowHeight = 720;

    [AvaloniaFact]
    public async Task 切换语言后界面文案立即变化()
    {
        var workspace = await TestWorkspace.CreateAsync();
        await using var _ = workspace;

        var services = TestServices.Create(workspace);
        await services.InitializeSeedDataAsync();

        var viewModel = services.CreateMainWindowViewModel();
        await viewModel.InitializeAsync();

        var window = new MainWindow
        {
            DataContext = viewModel,
            Width = WindowWidth,
            Height = WindowHeight,
        };
        window.Show();

        var send = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "SendButton");
        var newChat = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "NewSessionButton");

        Assert.Equal("发送", send.Content as string);
        // 新对话按钮是"图标 + 文字"，Content 不再是字符串，取里面的文字看
        Assert.Equal("新对话", TextOf(newChat));

        try
        {
            LocalizedResources.Apply(new LocalizationService(LocalizationService.English));

            // 没有重建控件，也没有手动刷绑定：文案自己就换了
            Assert.Equal("Send", send.Content as string);
            Assert.Equal("New chat", TextOf(newChat));
        }
        finally
        {
            // 资源字典是进程级共享的，用完必须还原，否则别的 UI 测试会看到英文
            LocalizedResources.Apply(new LocalizationService(LocalizationService.DefaultLanguage));
        }

        Assert.Equal("发送", send.Content as string);

        window.Close();
    }

    /// <summary>按钮里第一个可见的文字块。图标本身不产生 TextBlock，取到的就是文案。</summary>
    private static string? TextOf(Visual root) =>
        root.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(t => t.IsEffectivelyVisible)
            ?.Text;
}
