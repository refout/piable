using Piable.Models;
using Piable.Services;
using Piable.ViewModels;

namespace Piable.Tests.ViewModels;

public class ProviderConfigViewModelTests
{
    private static async Task<(TestWorkspace Workspace, ProviderConfigViewModel ViewModel)>
        CreateAsync()
    {
        var workspace = await TestWorkspace.CreateAsync();
        var services = TestServices.Create(workspace);
        await services.InitializeSeedDataAsync();

        var viewModel = new ProviderConfigViewModel(
            services.Config,
            new ModelListService(new HttpClient()),
            services.Orchestrator,
            new StubStatusReporter());

        await viewModel.LoadAsync();
        return (workspace, viewModel);
    }

    [Fact]
    public async Task 首次载入默认选中第一个预设且不落库()
    {
        var (workspace, vm) = await CreateAsync();
        await using var _w = workspace;

        Assert.NotNull(vm.SelectedPreset);
        Assert.Equal(ProviderPresets.All[0].Id, vm.SelectedPreset!.Id);

        // 只是展示，用户没点保存就不该产生配置记录
        var services = TestServices.Create(workspace);
        Assert.Empty(await services.Config.GetProvidersAsync());
    }

    [Fact]
    public async Task 选择预设后表单填入该预设的默认值()
    {
        var (workspace, vm) = await CreateAsync();
        await using var _w = workspace;

        vm.SelectedPreset = ProviderPresets.Get(ProviderPresets.DeepSeek);
        await WaitForAsync(() => vm.DefaultModel.Length > 0);

        Assert.Equal("https://api.deepseek.com/v1", vm.Endpoint);
        Assert.Equal("deepseek-chat", vm.DefaultModel);
        Assert.Equal("deepseek-chat", vm.Models[0]);
    }

    [Fact]
    public async Task 不支持动态获取模型的预设禁用获取按钮()
    {
        var (workspace, vm) = await CreateAsync();
        await using var _w = workspace;

        // Azure 用部署名寻址，没有模型列表接口
        vm.SelectedPreset = ProviderPresets.Get(ProviderPresets.AzureOpenAi);
        await WaitForAsync(() => vm.IsAzure);

        Assert.False(vm.SupportsModelFetch);
        Assert.False(vm.CanFetchModels, "点了必然失败的按钮不该可点");
    }

    [Fact]
    public async Task 支持动态获取模型的预设按钮可点()
    {
        var (workspace, vm) = await CreateAsync();
        await using var _w = workspace;

        vm.SelectedPreset = ProviderPresets.Get(ProviderPresets.OpenAi);
        await WaitForAsync(() => !vm.IsAzure);

        Assert.True(vm.SupportsModelFetch);
        Assert.True(vm.CanFetchModels);
    }

    [Fact]
    public async Task 执行中时获取按钮不可点()
    {
        var (workspace, vm) = await CreateAsync();
        await using var _w = workspace;

        vm.SelectedPreset = ProviderPresets.Get(ProviderPresets.OpenAi);
        await WaitForAsync(() => vm.SupportsModelFetch);

        vm.IsBusy = true;

        Assert.False(vm.CanFetchModels);
    }

    [Fact]
    public async Task 本地供应商无需APIKey()
    {
        var (workspace, vm) = await CreateAsync();
        await using var _w = workspace;

        vm.SelectedPreset = ProviderPresets.Get(ProviderPresets.Ollama);
        await WaitForAsync(() => !vm.RequiresApiKey);

        Assert.False(vm.RequiresApiKey);
        Assert.False(vm.IsAzure);
    }

    [Fact]
    public async Task 添加与移除模型()
    {
        var (workspace, vm) = await CreateAsync();
        await using var _w = workspace;

        vm.SelectedPreset = ProviderPresets.Get(ProviderPresets.DeepSeek);
        await WaitForAsync(() => vm.Models.Count > 0);

        var before = vm.Models.Count;
        vm.NewModelName = "my-model";
        vm.AddModelCommand.Execute(null);

        Assert.Equal(before + 1, vm.Models.Count);
        Assert.Contains("my-model", vm.Models);
        Assert.Equal(string.Empty, vm.NewModelName);

        vm.SelectedModel = "my-model";
        vm.RemoveModelCommand.Execute(null);

        Assert.DoesNotContain("my-model", vm.Models);
    }

    [Fact]
    public async Task 重复添加同名模型不生效()
    {
        var (workspace, vm) = await CreateAsync();
        await using var _w = workspace;

        vm.SelectedPreset = ProviderPresets.Get(ProviderPresets.OpenAi);
        await WaitForAsync(() => vm.Models.Count > 0);

        var before = vm.Models.Count;
        vm.NewModelName = vm.Models[0];
        vm.AddModelCommand.Execute(null);

        Assert.Equal(before, vm.Models.Count);
    }

    [Fact]
    public async Task 移除默认模型后自动改选其他模型()
    {
        var (workspace, vm) = await CreateAsync();
        await using var _w = workspace;

        vm.SelectedPreset = ProviderPresets.Get(ProviderPresets.OpenAi);
        await WaitForAsync(() => vm.Models.Count > 1);

        var removed = vm.DefaultModel;
        vm.SelectedModel = removed;
        vm.RemoveModelCommand.Execute(null);

        Assert.NotEqual(removed, vm.DefaultModel);
        Assert.False(string.IsNullOrWhiteSpace(vm.DefaultModel));
    }

    [Fact]
    public async Task 选择定价预设后填入价格()
    {
        var (workspace, vm) = await CreateAsync();
        await using var _w = workspace;

        var entry = ModelPricingPresets.All[0];
        vm.SelectedPricingPreset = entry;

        Assert.Equal(entry.InputPer1K, vm.InputPricePer1K);
        Assert.Equal(entry.OutputPer1K, vm.OutputPricePer1K);
    }

    [Fact]
    public async Task 保存后写入配置并成为默认()
    {
        var (workspace, vm) = await CreateAsync();
        await using var _w = workspace;

        vm.SelectedPreset = ProviderPresets.Get(ProviderPresets.OpenAi);
        await WaitForAsync(() => vm.DefaultModel.Length > 0);

        vm.ApiKey = "sk-test";
        await vm.SaveCommand.ExecuteAsync(null);

        var services = TestServices.Create(workspace);
        var saved = Assert.Single(await services.Config.GetProvidersAsync());
        Assert.Equal("sk-test", saved.ApiKey);
        Assert.True(saved.IsDefault);
        Assert.False(vm.IsStatusError);
    }

    /// <summary>
    /// 切换预设会走异步加载，等待条件成立。
    /// 不用固定延时是因为那会让测试在慢机器上变得不稳定。
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            await Task.Delay(10);
        }
    }
}
