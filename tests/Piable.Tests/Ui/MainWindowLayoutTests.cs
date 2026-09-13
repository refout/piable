using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using FluentIcons.Avalonia;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Piable.Helpers;
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
        var sendButton = Find<Button>(window, b => b.Name == "SendButton");

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
    public async Task 工具按钮与发送按钮在输入区排成一行_工具在左发送在右()
    {
        var (window, _, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        var agent = Find<Button>(window, b => b.Name == "AgentButton");
        var model = Find<Button>(window, b => b.Name == "ModelButton");
        var parameters = Find<Button>(window, b => b.Name == "ParametersButton");
        // 按名字找而不是按"发送"这段文字：按钮上的字已经外置进语言包，
        // 换一门语言文案就变了，按文字找控件会让测试跟着语言跑。
        var send = Find<Button>(window, b => b.Name == "SendButton");

        // 宽度必须大于零：被挤成零宽的按钮同样"可见"，不加这条断言的话，
        // 按钮被挤出布局也测不出来。
        foreach (var button in new[] { agent, model, parameters, send })
        {
            Assert.True(button.IsEffectivelyVisible, "工具行按钮应当可见");
            Assert.True(button.Bounds.Width > 0, "工具行按钮不能塌成零宽");
        }

        var agentPoint = agent.TranslatePoint(new Point(0, 0), window)!.Value;
        var modelPoint = model.TranslatePoint(new Point(0, 0), window)!.Value;
        var parametersPoint = parameters.TranslatePoint(new Point(0, 0), window)!.Value;
        var sendPoint = send.TranslatePoint(new Point(0, 0), window)!.Value;

        // 同一行：三个工具按钮与发送按钮的纵坐标一致
        Assert.Equal(agentPoint.Y, sendPoint.Y);
        Assert.Equal(agentPoint.Y, modelPoint.Y);
        Assert.Equal(agentPoint.Y, parametersPoint.Y);

        // 工具在左、发送在右
        Assert.True(agentPoint.X < modelPoint.X, "智能体按钮应在模型按钮左侧");
        Assert.True(modelPoint.X < parametersPoint.X, "模型按钮应在参数按钮左侧");
        Assert.True(parametersPoint.X < sendPoint.X, "工具按钮应整体位于发送按钮左侧");

        // 已迁到输入区：纵向落在窗口下半部，而不再贴着标题栏
        Assert.True(agentPoint.Y > WindowHeight / 2,
            $"工具行纵坐标 {agentPoint.Y} 应位于窗口下半部的输入区");

        // 且必须完整落在窗口内
        var right = sendPoint.X + send.Bounds.Width;
        Assert.True(right <= WindowWidth,
            $"发送按钮右边缘 {right} 超出了窗口宽度 {WindowWidth}");

        window.Close();
    }

    [AvaloniaFact]
    public async Task 发送按钮的文字在按钮内水平居中()
    {
        var (window, _, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        var send = Find<Button>(window, b => b.Name == "SendButton");
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Center, send.HorizontalContentAlignment);

        // 换成英文再量一次：两个汉字在无头环境下恰好把内容区填满，居不居中肉眼看不出来；
        // "Send" 明显比内容区窄，文字块有没有被拉伸一目了然。
        var original = Loc.Service;
        try
        {
            var service = new LocalizationService(LocalizationService.DefaultLanguage);
            Loc.Use(service);
            service.SetLanguage(LocalizationService.English);
            window.UpdateLayout();

            var text = send.GetVisualDescendants().OfType<TextBlock>().First();
            var contentWidth = send.Bounds.Width - send.Padding.Left - send.Padding.Right;

            Assert.True(text.Bounds.Width < contentWidth - 0.5,
                $"文字块被拉伸到 {text.Bounds.Width}（内容区仅 {contentWidth}），说明内容没有居中");

            var left = text.TranslatePoint(new Point(0, 0), send)!.Value.X;
            var gap = send.Bounds.Width - (left + text.Bounds.Width);
            Assert.True(Math.Abs(left - gap) < 1.5,
                $"文字左间距 {left} 与右间距 {gap} 不一致，文字没有居中");
        }
        finally
        {
            Loc.Use(original);
        }

        window.Close();
    }

    [AvaloniaFact]
    public async Task 智能体按钮显示当前智能体名称()
    {
        var (window, _, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        var button = Find<Button>(window, b => b.Name == "AgentButton");

        // 按钮上要显示当前智能体的名称，而不是一个无字的图标
        var texts = button.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text)
            .ToList();
        Assert.Contains(texts, t => t is not null && t.Contains("通用助手"));

        window.Close();
    }

    [AvaloniaFact]
    public async Task 模型按钮显示当前生效的模型()
    {
        var (window, viewModel, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        viewModel.CurrentSession!.Provider = new ProviderConfig
        {
            PresetId = ProviderPresets.OpenAi,
            DefaultModel = "gpt-4o-mini",
            Models = ["gpt-4o-mini", "gpt-4o"],
        };
        window.UpdateLayout();

        var button = Find<Button>(window, b => b.Name == "ModelButton");
        var texts = button.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text)
            .ToList();

        Assert.Contains(texts, t => t is not null && t.Contains("gpt-4o-mini"));

        window.Close();
    }

    [AvaloniaFact]
    public async Task 窗口收窄到最小宽度时工具行与发送按钮仍完整可见()
    {
        var (window, _, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        // 最坏情况：窗口收到最小宽度。工具行一边是四个按钮、另一边是发送按钮，
        // 一旦被挤出右边界用户就完全点不到，而这类问题在只测默认尺寸时发现不了。
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        window.UpdateLayout();

        var agent = Find<Button>(window, b => b.Name == "AgentButton");
        // 按名字找而不是按"发送"这段文字：按钮上的字已经外置进语言包，
        // 换一门语言文案就变了，按文字找控件会让测试跟着语言跑。
        var send = Find<Button>(window, b => b.Name == "SendButton");
        var newSession = Find<Button>(window, b => b.Name == "NewSessionButton");

        Assert.True(agent.IsEffectivelyVisible);
        Assert.True(agent.Bounds.Width > 0, "智能体按钮不能塌成零宽");
        Assert.True(send.IsEffectivelyVisible);
        Assert.True(send.Bounds.Width > 0, "发送按钮不能塌成零宽");
        Assert.True(newSession.IsEffectivelyVisible);
        Assert.True(newSession.Bounds.Width > 0, "新对话按钮不能塌成零宽");

        var agentLeft = agent.TranslatePoint(new Point(0, 0), window)!.Value;
        var sendLeft = send.TranslatePoint(new Point(0, 0), window)!.Value;
        var newSessionLeft = newSession.TranslatePoint(new Point(0, 0), window)!.Value;

        // 工具行最左边的按钮不能被挤出左边界
        Assert.True(newSessionLeft.X >= -0.5,
            $"新对话按钮左边缘 {newSessionLeft.X} 被挤出了窗口左侧");

        Assert.True(agentLeft.X + agent.Bounds.Width <= window.ClientSize.Width + 0.5,
            $"智能体按钮右边缘 {agentLeft.X + agent.Bounds.Width} 超出了客户区宽度 {window.ClientSize.Width}");

        var sendRight = sendLeft.X + send.Bounds.Width;
        Assert.True(sendRight <= window.ClientSize.Width + 0.5,
            $"发送按钮右边缘 {sendRight} 超出了客户区宽度 {window.ClientSize.Width}");

        // 挤到最窄也不能让工具按钮压到发送按钮上面去
        Assert.True(agentLeft.X + agent.Bounds.Width <= sendLeft.X + 0.5,
            "工具按钮与发送按钮重叠了");

        window.Close();
    }

    [AvaloniaFact]
    public async Task 输入区不再内联显示智能体选择与参数面板()
    {
        var (window, _, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        // 参数入口已收进输入区的按钮，未展开时界面上不该有滑块
        Assert.False(HasVisibleDescendant<Slider>(window),
            "参数滑块不应在未打开面板时就出现在界面上");

        // 输入区里也不该再有智能体下拉——它现在只存在于下拉面板中
        var inputBox = Find<TextBox>(window, t => t.Name == "InputBox");
        Assert.True(inputBox.IsEffectivelyVisible);
        Assert.False(HasVisibleDescendant<ComboBox>(window),
            "智能体与模型的选择器只应出现在展开的面板里");

        window.Close();
    }

    [AvaloniaFact]
    public async Task 展开智能体按钮后可直接点选智能体()
    {
        var (window, viewModel, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        var button = Find<Button>(window, b => b.Name == "AgentButton");
        var flyout = Assert.IsType<Flyout>(button.Flyout);

        flyout.ShowAt(button);
        window.UpdateLayout();

        var panel = Assert.IsType<StackPanel>(flyout.Content);
        var descendants = panel.GetVisualDescendants().ToList();

        var list = descendants.OfType<ListBox>().FirstOrDefault();
        Assert.NotNull(list);
        Assert.Same(viewModel.CurrentSession!.AvailableAgents, list.ItemsSource);
        Assert.Same(viewModel.CurrentSession.SelectedAgent, list.SelectedItem);

        // 关键回归点：列表项当场就能点，不必再展开一层下拉框。
        // ComboBox 只渲染当前选中项，其余要再点一次才出现，这里就是要卡住那种写法。
        Assert.Empty(descendants.OfType<ComboBox>());
        Assert.True(list.ItemCount > 1, "智能体列表应当一次列出多个可选项");

        foreach (var index in Enumerable.Range(0, list.ItemCount))
        {
            var item = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(index));
            Assert.True(item.IsEffectivelyVisible, $"第 {index} 个智能体应当直接可见可点");
        }

        // 参数滑块已经挪到「⚙ 参数」自己的面板里，智能体面板里不该再有
        Assert.Empty(descendants.OfType<Slider>());

        flyout.Hide();
        window.Close();
    }

    [AvaloniaFact]
    public async Task 展开模型按钮后可选择本次会话的模型()
    {
        var (window, viewModel, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        var session = viewModel.CurrentSession!;
        session.Provider = new ProviderConfig
        {
            Endpoint = "http://127.0.0.1/v1",
            ApiKey = "test",
            DefaultModel = "gpt-4o-mini",
            Models = ["gpt-4o-mini", "gpt-4o"],
        };

        var button = Find<Button>(window, b => b.Name == "ModelButton");
        var flyout = Assert.IsType<Flyout>(button.Flyout);

        flyout.ShowAt(button);
        window.UpdateLayout();

        var panel = Assert.IsType<StackPanel>(flyout.Content);
        var descendants = panel.GetVisualDescendants().ToList();

        // 模型列表跟随当前供应商
        var list = descendants.OfType<ListBox>().FirstOrDefault();
        Assert.NotNull(list);
        Assert.Same(session.AvailableModels, list.ItemsSource);
        Assert.Equal(new[] { "gpt-4o-mini", "gpt-4o" }, session.AvailableModels);
        Assert.Null(list.SelectedItem);   // 默认跟随供应商，没有覆盖

        // 选一个模型就等于给本会话开了覆盖
        list.SelectedItem = "gpt-4o";
        Assert.Equal("gpt-4o", session.EffectiveModel);
        Assert.True(session.HasModelOverride);

        // 「跟随默认」按钮把覆盖清掉
        var reset = descendants.OfType<Button>().FirstOrDefault();
        Assert.NotNull(reset);
        Assert.True(reset.Command!.CanExecute(null));

        flyout.Hide();
        window.Close();
    }

    [AvaloniaFact]
    public async Task 展开参数按钮后出现三个参数滑块()
    {
        var (window, viewModel, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        var button = Find<Button>(window, b => b.Name == "ParametersButton");
        var flyout = Assert.IsType<Flyout>(button.Flyout);

        flyout.ShowAt(button);
        window.UpdateLayout();

        var panel = Assert.IsType<StackPanel>(flyout.Content);
        var descendants = panel.GetVisualDescendants().ToList();

        // Temperature / Top P / Max Tokens 三个滑块
        Assert.Equal(3, descendants.OfType<Slider>().Count());
        Assert.NotEmpty(descendants.OfType<Button>());   // 重置按钮

        // 参数面板只管采样参数，不该混入智能体/模型选择器
        Assert.Empty(descendants.OfType<ComboBox>());
        Assert.Empty(descendants.OfType<ListBox>());

        flyout.Hide();
        window.Close();
    }

    private static bool HasVisibleDescendant<T>(Visual root) where T : Visual =>
        root.GetVisualDescendants().OfType<T>().Any(v => v.IsEffectivelyVisible);

    /// <summary>
    /// 等待一个异步生效的条件。
    /// 删除会话这类操作在命令内部是 await 串起来的，命令本身又是即发即忘，
    /// 用固定延时会在慢机器上变得不稳定。
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            await Task.Delay(10);
        }
    }

    [AvaloniaFact]
    public async Task 删除最后一个会话后自动新建一个空会话()
    {
        var (window, viewModel, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        var only = viewModel.Sessions.Single();
        var deletedId = only.Id;

        // 走完整的二次确认：第一次点击只是进入确认态
        only.RequestDeleteCommand.Execute(null);
        only.RequestDeleteCommand.Execute(null);

        await WaitForAsync(() => viewModel.Sessions.SingleOrDefault()?.Id != deletedId);

        // 回归点：会话全删光后右侧不能继续停留在已删除的那个会话上——
        // 它已不在列表里，再往里发消息会写到一个不存在的会话 id 上。
        var current = Assert.Single(viewModel.Sessions);
        Assert.NotEqual(deletedId, current.Id);
        Assert.NotNull(viewModel.CurrentSession);
        Assert.Equal(current.Id, viewModel.CurrentSession!.Session.Id);
        Assert.True(viewModel.CurrentSession.Session.IsEmpty);

        window.Close();
    }

    [AvaloniaFact]
    public async Task 重命名后标题与侧边栏同步并落库()
    {
        var (window, viewModel, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        var button = Find<Button>(window, b => b.Name == "SessionRenameButton");
        Assert.True(button.IsEffectivelyVisible, "重命名入口应当可见");

        viewModel.BeginRenameSessionCommand.Execute(null);

        Assert.True(viewModel.IsRenamingSession);
        Assert.Equal(SessionTitleGenerator.DefaultTitle, viewModel.SessionTitleDraft);

        viewModel.SessionTitleDraft = "关于 AI 的讨论";
        await viewModel.CommitRenameSessionCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsRenamingSession);
        Assert.Equal("关于 AI 的讨论", viewModel.CurrentSession!.Title);
        Assert.Equal("关于 AI 的讨论", viewModel.Sessions.Single().Title);

        var reloaded = await TestServices.Create(workspace).Sessions
            .LoadAsync(viewModel.CurrentSession.Session.Id);
        Assert.Equal("关于 AI 的讨论", reloaded!.Title);

        window.Close();
    }

    [AvaloniaFact]
    public async Task 取消或留空的重命名不会改动标题()
    {
        var (window, viewModel, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        viewModel.BeginRenameSessionCommand.Execute(null);
        viewModel.SessionTitleDraft = "改了一半";
        viewModel.CancelRenameSessionCommand.Execute(null);

        Assert.False(viewModel.IsRenamingSession);
        Assert.Equal(SessionTitleGenerator.DefaultTitle, viewModel.CurrentSession!.Title);

        // 空标题同样不生效：留一个没有名字的会话比保留原名更糟
        viewModel.BeginRenameSessionCommand.Execute(null);
        viewModel.SessionTitleDraft = "   ";
        await viewModel.CommitRenameSessionCommand.ExecuteAsync(null);

        Assert.Equal(SessionTitleGenerator.DefaultTitle, viewModel.CurrentSession.Title);

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
    public async Task 折叠按钮用矢量图标而不是字符或自绘图形()
    {
        var (window, viewModel, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        var toggle = Find<Button>(window, b => b.Name == "LeftPanelToggleButton");
        Assert.True(toggle.IsEffectivelyVisible);

        // 关键回归点：图标不能是 "≡"（U+2261 数学等号）——
        // 那条字符在多数字体里渲染成又短又挤的三横线，看着窄。
        Assert.DoesNotContain(toggle.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text))
            .Select(t => t.Text), t => t == "≡");

        // 也不能退回自绘的 Rectangle：图标统一由 FluentIcons 提供，单色矢量、跟随主题色
        Assert.Empty(toggle.GetVisualDescendants().OfType<Rectangle>());
        Assert.NotEmpty(toggle.GetVisualDescendants().OfType<FluentIcon>());

        // 折叠态下 narrow 样式只改对齐，不该把图标藏起来
        viewModel.ToggleLeftPanelCommand.Execute(null);
        window.UpdateLayout();
        Assert.True(toggle.IsEffectivelyVisible);
        Assert.NotEmpty(toggle.GetVisualDescendants().OfType<FluentIcon>()
            .Where(i => i.IsEffectivelyVisible));

        window.Close();
    }

    [AvaloniaFact]
    public async Task 新对话按钮在输入区最左且折叠侧栏后仍可用()
    {
        var (window, viewModel, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        var newSession = Find<Button>(window, b => b.Name == "NewSessionButton");
        var agent = Find<Button>(window, b => b.Name == "AgentButton");

        Assert.True(newSession.IsEffectivelyVisible, "新对话按钮应当可见");

        var newSessionX = newSession.TranslatePoint(new Point(0, 0), window)!.Value.X;
        var agentX = agent.TranslatePoint(new Point(0, 0), window)!.Value.X;
        Assert.True(newSessionX < agentX, "新对话按钮应在智能体按钮左侧");

        // 它现在属于输入区而不是侧边栏：折叠侧栏不该把它一起藏掉
        viewModel.ToggleLeftPanelCommand.Execute(null);
        window.UpdateLayout();

        Assert.True(viewModel.IsLeftPanelCollapsed);
        Assert.True(newSession.IsEffectivelyVisible, "折叠后新对话按钮应当仍然可用");

        window.Close();
    }

    [AvaloniaFact]
    public async Task 配置入口展开时占满侧栏_折叠后收成齿轮图标()
    {
        var (window, viewModel, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        var config = Find<Button>(window, b => b.Name == "ToggleConfigButton");
        var leftPanel = Find<Border>(window, b => b.Name == "LeftPanel");

        // 展开态：横向占满侧边栏（只差 StackPanel 的左右 Margin），并显示"配置"二字
        Assert.True(config.IsEffectivelyVisible);
        Assert.True(config.Bounds.Width >= leftPanel.Bounds.Width - 16,
            $"配置按钮宽 {config.Bounds.Width} 应占满侧边栏 {leftPanel.Bounds.Width}");
        Assert.Contains(VisibleTexts(config), t => t == "配置");

        viewModel.ToggleLeftPanelCommand.Execute(null);
        window.UpdateLayout();

        // 折叠态：入口不能消失，只是退化成一个图标，否则收起侧栏就进不去配置了
        var texts = VisibleTexts(config);
        Assert.True(config.IsEffectivelyVisible, "折叠后配置入口应当仍然可见");
        Assert.NotEmpty(config.GetVisualDescendants().OfType<FluentIcon>()
            .Where(i => i.IsEffectivelyVisible));
        Assert.DoesNotContain(texts, t => t == "配置");
        Assert.True(config.Bounds.Width <= 48, "折叠后配置入口应收缩到侧边栏宽度内");

        window.Close();
    }

    private static List<string?> VisibleTexts(Visual root) =>
        root.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible)
            .Select(t => t.Text)
            .ToList();

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

        viewModel.ReportError("认证失败，请检查 API Key");
        window.UpdateLayout();

        Assert.Equal("认证失败，请检查 API Key", viewModel.StatusMessage);
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
    public async Task 配置页可加载供应商列表与内置智能体()
    {
        var (window, viewModel, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        viewModel.ToggleConfigViewCommand.Execute(null);
        await viewModel.ProviderConfig.LoadAsync();
        await viewModel.AgentConfig.LoadAsync();
        window.UpdateLayout();

        // 内置预设各占一项；自定义供应商保存后会追加在后面
        Assert.Equal(ProviderPresets.All.Count, viewModel.ProviderConfig.Choices.Count);
        Assert.Equal(2, viewModel.AgentConfig.Agents.Count);
        Assert.NotNull(viewModel.AgentConfig.SelectedAgent);

        window.Close();
    }

    [AvaloniaFact]
    public async Task 配置页可新建自定义供应商并填入名称()
    {
        var (window, viewModel, workspace) = await CreateWindowAsync();
        await using var _ = workspace;

        viewModel.ToggleConfigViewCommand.Execute(null);
        await viewModel.ProviderConfig.LoadAsync();

        var vm = viewModel.ProviderConfig;
        var before = vm.Choices.Count;

        vm.CreateCustomCommand.Execute(null);
        window.UpdateLayout();

        Assert.Equal(before + 1, vm.Choices.Count);
        Assert.Same(vm.Choices[^1], vm.SelectedChoice);
        Assert.True(vm.IsCustomProvider, "新建出来的应当是自定义供应商");
        Assert.True(vm.DeleteCustomCommand.CanExecute(null), "自定义供应商应当可以删除");

        // 名称输入框只在自定义供应商时出现
        vm.ProviderName = "公司网关";
        Assert.Equal("公司网关", vm.SelectedChoice!.DisplayName);

        var nameBox = window.GetVisualDescendants().OfType<TextBox>()
            .FirstOrDefault(t => t.Text == "公司网关");
        Assert.NotNull(nameBox);
        Assert.True(nameBox!.IsEffectivelyVisible);

        window.Close();
    }
}
