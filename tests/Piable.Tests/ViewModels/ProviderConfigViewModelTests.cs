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
        await WaitForAsync(() => vm.Endpoint.Length > 0);

        Assert.Equal("https://api.deepseek.com/v1", vm.Endpoint);
        // DeepSeek 不预置模型列表，表单默认空，靠"获取模型列表"拉取
        Assert.Equal(string.Empty, vm.DefaultModel);
        Assert.Empty(vm.Models);
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

    // ---------------- 自定义供应商 ----------------

    [Fact]
    public async Task 新建自定义供应商默认不落库()
    {
        var (workspace, vm) = await CreateAsync();
        await using var _w = workspace;

        var before = vm.Choices.Count;
        vm.CreateCustomCommand.Execute(null);

        Assert.Equal(before + 1, vm.Choices.Count);
        Assert.Same(vm.Choices[^1], vm.SelectedChoice);
        Assert.True(vm.IsCustomProvider);
        Assert.NotEmpty(vm.ProviderName);
        Assert.Equal(vm.ProviderName, vm.SelectedChoice!.DisplayName);

        // 只是新建还没保存，不该在库里留下空配置
        var services = TestServices.Create(workspace);
        Assert.Empty(await services.Config.GetProvidersAsync());
    }

    [Fact]
    public async Task 自定义供应商不强制APIKey且可尝试拉取模型()
    {
        var (workspace, vm) = await CreateAsync();
        await using var _w = workspace;

        vm.CreateCustomCommand.Execute(null);

        // 自建端点形形色色，本地的可能根本不要 Key，逼用户先猜一次没有意义
        Assert.False(vm.RequiresApiKey);
        Assert.True(vm.SupportsModelFetch);
        Assert.False(vm.IsAzure);
    }

    [Fact]
    public async Task 自定义供应商命名保存后可再次载入()
    {
        var (workspace, vm) = await CreateAsync();
        await using var _w = workspace;

        vm.CreateCustomCommand.Execute(null);
        vm.ProviderName = "公司网关";
        vm.Endpoint = "https://gw.example.com/v1";
        vm.NewModelName = "my-model";
        vm.AddModelCommand.Execute(null);
        await vm.SaveCommand.ExecuteAsync(null);

        var services = TestServices.Create(workspace);
        var saved = Assert.Single(await services.Config.GetProvidersAsync());
        Assert.Equal("公司网关", saved.Name);
        Assert.True(saved.IsCustom);
        Assert.Equal("https://gw.example.com/v1", saved.Endpoint);

        // 重开配置页：下拉里要出现这条自定义供应商，并直接落在它上面
        await vm.LoadAsync();
        Assert.Equal(ProviderPresets.All.Count + 1, vm.Choices.Count);
        Assert.Equal("公司网关", vm.SelectedChoice!.DisplayName);
        Assert.Equal("https://gw.example.com/v1", vm.Endpoint);
    }

    [Fact]
    public async Task 可以创建多条自定义供应商且互不干扰()
    {
        var (workspace, vm) = await CreateAsync();
        await using var _w = workspace;

        vm.CreateCustomCommand.Execute(null);
        vm.ProviderName = "公司网关";
        vm.Endpoint = "https://gw.example.com/v1";
        await vm.SaveCommand.ExecuteAsync(null);

        vm.CreateCustomCommand.Execute(null);
        // 默认名要避开已经存在的那条，否则下拉里两条同名没法分辨
        Assert.NotEqual("公司网关", vm.ProviderName);
        vm.ProviderName = "本地 vLLM";
        vm.Endpoint = "http://127.0.0.1:8000/v1";
        await vm.SaveCommand.ExecuteAsync(null);

        var services = TestServices.Create(workspace);
        var all = await services.Config.GetProvidersAsync();
        Assert.Equal(2, all.Count(p => p.IsCustom));

        // 切回第一条：表单要换成它的配置
        vm.SelectedChoice = vm.Choices.First(c => c.DisplayName == "公司网关");
        Assert.Equal("https://gw.example.com/v1", vm.Endpoint);

        vm.SelectedChoice = vm.Choices.First(c => c.DisplayName == "本地 vLLM");
        Assert.Equal("http://127.0.0.1:8000/v1", vm.Endpoint);
    }

    [Fact]
    public async Task 删除自定义供应商后落到相邻项()
    {
        var (workspace, vm) = await CreateAsync();
        await using var _w = workspace;

        foreach (var (name, endpoint) in new[]
                 {
                     ("公司网关", "https://gw.example.com/v1"),
                     ("本地 vLLM", "http://127.0.0.1:8000/v1"),
                 })
        {
            vm.CreateCustomCommand.Execute(null);
            vm.ProviderName = name;
            vm.Endpoint = endpoint;
            await vm.SaveCommand.ExecuteAsync(null);
        }

        await vm.DeleteCustomCommand.ExecuteAsync(null);

        var services = TestServices.Create(workspace);
        var all = await services.Config.GetProvidersAsync();
        Assert.DoesNotContain(all, p => p.Name == "本地 vLLM");
        Assert.Single(all);

        // 删掉的是最后一条时落在前一条上，而不是把用户弹回列表顶端
        Assert.Equal("公司网关", vm.SelectedChoice!.DisplayName);

        // 内置预设不能删：它是界面的一部分，用户想做的是清空配置而不是让它消失
        vm.SelectedChoice = vm.Choices.First(c => c.IsBuiltIn);
        Assert.False(vm.DeleteCustomCommand.CanExecute(null));
    }

    [Fact]
    public async Task 删掉默认供应商后会自动另立默认()
    {
        var (workspace, vm) = await CreateAsync();
        await using var _w = workspace;

        vm.CreateCustomCommand.Execute(null);
        vm.ProviderName = "公司网关";
        vm.Endpoint = "https://gw.example.com/v1";
        await vm.SaveCommand.ExecuteAsync(null);

        vm.CreateCustomCommand.Execute(null);
        vm.ProviderName = "本地 vLLM";
        vm.Endpoint = "http://127.0.0.1:8000/v1";
        await vm.SaveCommand.ExecuteAsync(null);

        var services = TestServices.Create(workspace);
        var before = await services.Config.GetProvidersAsync();
        var defaultId = before.Single(p => p.IsDefault).Id;

        // 删掉当前默认那一条，剩下的必须有人接班，
        // 否则用户发起对话时会莫名其妙地没有可用供应商
        vm.SelectedChoice = vm.Choices.First(c => c.Provider?.Id == defaultId);
        await vm.DeleteCustomCommand.ExecuteAsync(null);

        var after = await services.Config.GetProvidersAsync();
        Assert.Single(after);
        Assert.True(after[0].IsDefault);
    }

    [Fact]
    public async Task 自定义供应商的名字为空时保存给出默认名()
    {
        var (workspace, vm) = await CreateAsync();
        await using var _w = workspace;

        vm.CreateCustomCommand.Execute(null);
        vm.ProviderName = "   ";
        vm.Endpoint = "https://gw.example.com/v1";
        await vm.SaveCommand.ExecuteAsync(null);

        var services = TestServices.Create(workspace);
        var saved = Assert.Single(await services.Config.GetProvidersAsync());
        // 空名字会让下拉里冒出一条没法辨认的条目，宁可退一个默认名
        Assert.False(string.IsNullOrWhiteSpace(saved.Name));
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
