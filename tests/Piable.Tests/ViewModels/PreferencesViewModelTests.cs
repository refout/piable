using Piable.ViewModels;

namespace Piable.Tests.ViewModels;

/// <summary>
/// 偏好设置页的落库行为。
/// 这一页是"改动即存"，没有保存按钮，因此每个开关都必须自己写库，
/// 缺了哪个就表现为"界面上改了、下次启动又变回去"。
/// </summary>
public class PreferencesViewModelTests
{
    private static async Task<(TestWorkspace Workspace, TestServices Services, PreferencesViewModel ViewModel)>
        CreateAsync()
    {
        var workspace = await TestWorkspace.CreateAsync();
        var services = TestServices.Create(workspace);
        await services.InitializeSeedDataAsync();

        var viewModel = new PreferencesViewModel(services.Config, new StubStatusReporter());
        await viewModel.LoadAsync();

        return (workspace, services, viewModel);
    }

    [Fact]
    public async Task 工具轮数上限改动后落库()
    {
        var (workspace, services, vm) = await CreateAsync();
        await using var _ = workspace;

        Assert.Equal(5, vm.MaxToolRounds);

        vm.MaxToolRounds = 8;
        await WaitForAsync(() => services.Config.GetPreferencesAsync().Result.MaxToolRounds == 8);

        Assert.Equal(8, (await services.Config.GetPreferencesAsync()).MaxToolRounds);
    }

    [Fact]
    public async Task 工具轮数被夹在一到二十之间()
    {
        var (workspace, services, vm) = await CreateAsync();
        await using var _ = workspace;

        // 0 轮等于彻底禁用工具，界面上不允许，落库时再兜一次
        vm.MaxToolRounds = 0;
        await WaitForAsync(() => services.Config.GetPreferencesAsync().Result.MaxToolRounds == 1);

        Assert.Equal(1, (await services.Config.GetPreferencesAsync()).MaxToolRounds);
    }

    [Fact]
    public async Task 请求超时改动后落库()
    {
        var (workspace, services, vm) = await CreateAsync();
        await using var _ = workspace;

        vm.RequestTimeoutSeconds = 600;
        await WaitForAsync(() => services.Config.GetPreferencesAsync().Result.RequestTimeoutSeconds == 600);

        Assert.Equal(600, (await services.Config.GetPreferencesAsync()).RequestTimeoutSeconds);
    }

    /// <summary>等待即发即忘的保存落到库里。</summary>
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            await Task.Delay(10);
        }
    }
}
