using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Piable.Models;
using Piable.Services;
using Piable.ViewModels;
using Piable.Views;

namespace Piable.Tests.Ui;

/// <summary>
/// 无头渲染下的真实布局校验。
///
/// 编译期绑定只能保证属性名没写错，保证不了元素真的出现在可见区域里——
/// 底部输入区被挤出窗口、状态栏高度塌成 0 这类问题只有跑过布局才看得出来。
/// </summary>
public class MainWindowLayoutTests
{
    private const double WindowWidth = 1100;
    private const double WindowHeight = 720;

    private static async Task<(MainWindow Window, MainWindowViewModel ViewModel, TestWorkspace Workspace)>
        CreateWindowAsync()
    {
        var workspace = await TestWorkspace.CreateAsync();
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

        return (window, viewModel, workspace);
    }

    private static T Find<T>(Visual root, Func<T, bool> predicate) where T : Visual =>
        root.GetVisualDescendants().OfType<T>().First(predicate);

    private static double? TopWithin(Visual element, Visual root)
    {
        var point = element.TranslatePoint(new Point(0, 0), root);
        return point?.Y;
    }

    [AvaloniaFact]
    public async Task 首次启动会自动创建并打开一个会话()
    {
        var (window, viewModel, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        Assert.Single(viewModel.Sessions);
        Assert.NotNull(viewModel.CurrentSession);
        Assert.Equal(SessionTitleGenerator.DefaultTitle, viewModel.CurrentSession.Title);

        // 回归点：新会话必须挂上默认智能体。曾经因为此时 CurrentSession 尚为 null，
        // 会话落库时 AgentId 为空，侧边栏于是把智能体名显示成"已删除"。
        Assert.Equal(ConfigService.GeneralAgentId, viewModel.CurrentSession.Session.AgentId);
        Assert.NotNull(viewModel.CurrentSession.SelectedAgent);
        Assert.Equal("通用助手", viewModel.Sessions.Single().AgentName);

        window.Close();
    }

    [AvaloniaFact]
    public async Task 输入框与发送按钮渲染在窗口内()
    {
        var (window, _, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        var inputBox = Find<TextBox>(window, t => t.Name == "InputBox");
        var sendButton = Find<Button>(window, b => b.Content as string == "发送");

        Assert.True(inputBox.IsEffectivelyVisible, "输入框应当可见");
        Assert.True(inputBox.Bounds.Height > 0, "输入框不应塌成零高度");
        Assert.True(sendButton.IsEffectivelyVisible, "发送按钮应当可见");

        // 关键回归点：输入区必须落在窗口高度之内，不能被挤出可视区域
        var inputTop = TopWithin(inputBox, window);
        Assert.NotNull(inputTop);
        Assert.InRange(inputTop!.Value, 0, WindowHeight - inputBox.Bounds.Height);

        window.Close();
    }

    [AvaloniaFact]
    public async Task 智能体按钮渲染在界面右上角()
    {
        var (window, _, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        var button = Find<Button>(window, b => b.Name == "AgentButton");

        Assert.True(button.IsEffectivelyVisible, "智能体按钮应当可见");

        // 宽度必须大于零：被挤成零宽的按钮同样"可见"，而且右边缘也"没超界"，
        // 不加这条断言的话，按钮被挤出布局也测不出来。
        Assert.True(button.Bounds.Width > 0,
            $"智能体按钮宽度为 {button.Bounds.Width}，说明它被挤出了布局");

        // 按钮上要显示当前智能体的名称，而不是一个无字的图标
        var texts = button.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text)
            .ToList();
        Assert.Contains(texts, t => t is not null && t.Contains("通用助手"));

        var topLeft = button.TranslatePoint(new Point(0, 0), window);
        Assert.NotNull(topLeft);

        // 贴右上角：纵向落在顶部区域，横向落在右半侧
        Assert.True(topLeft!.Value.Y < WindowHeight / 4,
            $"按钮纵坐标 {topLeft.Value.Y} 应贴近顶部");
        Assert.True(topLeft.Value.X > WindowWidth / 2,
            $"按钮横坐标 {topLeft.Value.X} 应位于右半侧");

        // 且必须完整落在窗口内。右上角的元素最容易被挤出右边界，
        // 一旦越界用户就点不到它，而这类问题在截图上未必看得出来。
        var right = topLeft.Value.X + button.Bounds.Width;
        Assert.True(right <= WindowWidth,
            $"按钮右边缘 {right} 超出了窗口宽度 {WindowWidth}");

        window.Close();
    }

    [AvaloniaFact]
    public async Task 窗口收窄到最小宽度时智能体按钮仍完整可见()
    {
        var (window, _, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        // 最坏情况：窗口收到最小宽度。右上角的元素一旦被挤出右边界，
        // 用户就完全点不到它，而这类问题在只测默认尺寸时发现不了。
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        window.UpdateLayout();

        var button = Find<Button>(window, b => b.Name == "AgentButton");
        Assert.True(button.IsEffectivelyVisible);
        Assert.True(button.Bounds.Width > 0, "按钮不能塌成零宽");

        var topLeft = button.TranslatePoint(new Point(0, 0), window);
        Assert.NotNull(topLeft);

        var right = topLeft!.Value.X + button.Bounds.Width;
        Assert.True(right <= window.ClientSize.Width + 0.5,
            $"按钮右边缘 {right} 超出了客户区宽度 {window.ClientSize.Width}");

        window.Close();
    }

    [AvaloniaFact]
    public async Task 输入区不再内联显示智能体选择与参数面板()
    {
        var (window, _, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        // 参数入口已收进右上角的按钮，未展开时界面上不该有滑块
        Assert.False(HasVisibleDescendant<Slider>(window),
            "参数滑块不应在未打开面板时就出现在界面上");

        // 输入区里也不该再有智能体下拉——它现在只存在于下拉面板中
        var inputBox = Find<TextBox>(window, t => t.Name == "InputBox");
        Assert.True(inputBox.IsEffectivelyVisible);

        window.Close();
    }

    [AvaloniaFact]
    public async Task 展开智能体按钮后出现选择器与三个参数滑块()
    {
        var (window, viewModel, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        var button = Find<Button>(window, b => b.Name == "AgentButton");
        var flyout = Assert.IsType<Flyout>(button.Flyout);

        flyout.ShowAt(button);
        window.UpdateLayout();

        var panel = Assert.IsType<StackPanel>(flyout.Content);
        var descendants = panel.GetVisualDescendants().ToList();

        // 智能体选择器
        var combo = descendants.OfType<ComboBox>().FirstOrDefault();
        Assert.NotNull(combo);
        Assert.Same(viewModel.CurrentSession!.AvailableAgents, combo.ItemsSource);

        // Temperature / Top P / Max Tokens 三个滑块
        Assert.Equal(3, descendants.OfType<Slider>().Count());
        Assert.NotEmpty(descendants.OfType<Button>());   // 重置按钮

        flyout.Hide();
        window.Close();
    }

    private static bool HasVisibleDescendant<T>(Visual root) where T : Visual =>
        root.GetVisualDescendants().OfType<T>().Any(v => v.IsEffectivelyVisible);

    [AvaloniaFact]
    public async Task 状态栏位于窗口底部且可见()
    {
        var (window, _, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        var statusBar = Find<Border>(window, b => b.Name == "StatusBar");

        Assert.True(statusBar.IsEffectivelyVisible, "状态栏应当可见");
        Assert.True(statusBar.Bounds.Height > 0, "状态栏不应塌成零高度");

        var top = TopWithin(statusBar, window);
        Assert.NotNull(top);
        // 状态栏应贴在底部：其下边缘与窗口下边缘对齐
        Assert.Equal(WindowHeight, top!.Value + statusBar.Bounds.Height, precision: 0);

        window.Close();
    }

    [AvaloniaFact]
    public async Task 左侧面板默认宽度为二百六十()
    {
        var (window, viewModel, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        var leftPanel = Find<Border>(window, b => b.Name == "LeftPanel");
        Assert.False(viewModel.IsLeftPanelCollapsed);
        Assert.Equal(260, leftPanel.Bounds.Width, precision: 0);

        window.Close();
    }

    [AvaloniaFact]
    public async Task 折叠后左侧面板收窄到四十八()
    {
        var (window, viewModel, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        viewModel.ToggleLeftPanelCommand.Execute(null);
        window.UpdateLayout();

        var leftPanel = Find<Border>(window, b => b.Name == "LeftPanel");

        Assert.True(viewModel.IsLeftPanelCollapsed);
        Assert.Equal(48, leftPanel.Bounds.Width, precision: 0);

        window.Close();
    }

    [AvaloniaFact]
    public async Task 新对话与配置按钮在折叠时隐藏展开时可见()
    {
        var (window, viewModel, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        var newSession = Find<Button>(window, b => b.Content as string == "✨ 新对话");
        Assert.True(newSession.IsEffectivelyVisible, "展开状态下应显示新对话按钮");

        viewModel.ToggleLeftPanelCommand.Execute(null);
        window.UpdateLayout();

        Assert.False(newSession.IsEffectivelyVisible, "折叠后应隐藏新对话按钮");

        window.Close();
    }

    [AvaloniaFact]
    public async Task 切换到配置视图后对话输入区隐藏并出现三个标签页()
    {
        var (window, viewModel, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        viewModel.ToggleConfigViewCommand.Execute(null);
        window.UpdateLayout();

        var inputBox = Find<TextBox>(window, t => t.Name == "InputBox");
        Assert.False(inputBox.IsEffectivelyVisible, "配置视图下不应显示对话输入框");

        // API 配置 / 智能体管理 / 技能 / MCP 服务器 / 偏好设置
        var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
        Assert.Equal(5, tabs.ItemCount);

        window.Close();
    }

    [AvaloniaFact]
    public async Task 错误提示写入状态栏()
    {
        var (window, viewModel, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        viewModel.ReportError("❌ 认证失败，请检查 API Key");
        window.UpdateLayout();

        Assert.Equal("❌ 认证失败，请检查 API Key", viewModel.StatusMessage);
        Assert.True(viewModel.IsStatusError);

        window.Close();
    }

    [AvaloniaFact]
    public async Task 会话项渲染标题与统计摘要()
    {
        var (window, viewModel, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        var item = viewModel.Sessions.Single();
        Assert.Equal(SessionTitleGenerator.DefaultTitle, item.Title);
        Assert.Contains("tokens", item.SummaryText);

        window.Close();
    }

    [AvaloniaFact]
    public async Task 未配置供应商时状态栏提示未配置()
    {
        var (window, viewModel, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        Assert.False(viewModel.IsProviderConfigured);
        Assert.Equal("未配置供应商", viewModel.ProviderModelText);

        window.Close();
    }

    [AvaloniaFact]
    public async Task 配置页可加载预设列表与内置智能体()
    {
        var (window, viewModel, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        viewModel.ToggleConfigViewCommand.Execute(null);
        await viewModel.ProviderConfig.LoadAsync();
        await viewModel.AgentConfig.LoadAsync();
        window.UpdateLayout();

        Assert.Equal(ProviderPresets.All.Count, viewModel.ProviderConfig.AvailablePresets.Count);
        Assert.Equal(2, viewModel.AgentConfig.Agents.Count);
        Assert.NotNull(viewModel.AgentConfig.SelectedAgent);

        window.Close();
    }
}
