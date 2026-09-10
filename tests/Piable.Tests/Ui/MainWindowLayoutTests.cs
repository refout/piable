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

        var config = new ConfigService(workspace.Providers, workspace.Agents, workspace.Preferences);
        await config.InitializeAsync();

        var sessions = new SessionService(workspace.Sessions);
        var calculator = new TokenCostCalculator();
        var orchestrator = new AgentOrchestrator(new ChatClientFactory());

        var viewModel = new MainWindowViewModel(
            config, sessions, orchestrator, calculator, new ModelListService(new HttpClient()));

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

        // API 配置 / 智能体管理 / 偏好设置
        var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
        Assert.Equal(3, tabs.ItemCount);

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

        Assert.False(viewModel.IsConnected);
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
