using Piable.Models;
using Piable.Services;
using Piable.Services.Tools;
using Piable.ViewModels;

namespace Piable.Tests.ViewModels;

public class AgentConfigViewModelTests
{
    private static async Task<(TestWorkspace Workspace, AgentConfigViewModel ViewModel, StubStatusReporter Status)>
        CreateAsync()
    {
        var workspace = await TestWorkspace.CreateAsync();
        var services = TestServices.Create(workspace);
        await services.InitializeSeedDataAsync();

        var status = new StubStatusReporter();
        var viewModel = new AgentConfigViewModel(services.Config, services.Skills, status);
        await viewModel.LoadAsync();

        return (workspace, viewModel, status);
    }

    [Fact]
    public async Task 载入后列出内置智能体并选中默认项()
    {
        var (workspace, vm, _) = await CreateAsync();
        await using var _w = workspace;

        Assert.Equal(2, vm.Agents.Count);
        Assert.NotNull(vm.SelectedAgent);
        Assert.True(vm.SelectedAgent!.IsDefault);
    }

    [Fact]
    public async Task 内置智能体不可删除_自定义的可删除()
    {
        var (workspace, vm, _) = await CreateAsync();
        await using var _w = workspace;

        var builtIn = vm.Agents.Single(a => a.Id == ConfigService.GeneralAgentId);
        Assert.False(builtIn.CanDelete);

        var custom = await CreateCustomAgentAsync(vm, "可删除的助手");
        Assert.True(custom.CanDelete);
    }

    [Fact]
    public async Task 点击新建不会立刻写库()
    {
        var (workspace, vm, _) = await CreateAsync();
        await using var _w = workspace;

        var before = vm.Agents.Count;

        vm.NewAgentCommand.Execute(null);

        // 只是打开一张空表单，用户点保存之前不应产生任何记录，
        // 否则每次误点"新建"都会永久多出一条垃圾数据
        Assert.Equal(before, vm.Agents.Count);

        var persisted = await TestServices.Create(workspace).Config.GetAgentsAsync();
        Assert.DoesNotContain(persisted, a => a.Name == "新智能体");
    }

    [Fact]
    public async Task 删除内置智能体时如实报告失败而不是谎报成功()
    {
        var (workspace, vm, status) = await CreateAsync();
        await using var _w = workspace;

        // 界面已隐藏内置项的删除按钮，这里直接调用以验证兜底行为：
        // 存储层是静默拒绝的，不能因此对用户报"已删除"
        await vm.DeleteAsync(ConfigService.GeneralAgentId);

        Assert.Contains(status.Errors, e => e.Contains("内置"));
        Assert.Empty(status.Successes);
        Assert.Contains(vm.Agents, a => a.Id == ConfigService.GeneralAgentId);
    }

    [Fact]
    public async Task 删除自定义智能体成功()
    {
        var (workspace, vm, status) = await CreateAsync();
        await using var _w = workspace;

        var custom = await CreateCustomAgentAsync(vm, "待删除的助手");

        await vm.DeleteAsync(custom.Id);

        Assert.Contains(status.Successes, s => s.Contains("已删除"));
        Assert.DoesNotContain(vm.Agents, a => a.Id == custom.Id);
    }

    /// <summary>走完整的"新建草稿 → 保存"流程，得到一个已落库的自定义智能体。</summary>
    private static async Task<AgentListItemViewModel> CreateCustomAgentAsync(
        AgentConfigViewModel vm, string name)
    {
        vm.NewAgentCommand.Execute(null);
        vm.Name = name;
        await vm.SaveCommand.ExecuteAsync(null);

        return vm.Agents.Single(a => a.Name == name);
    }

    [Fact]
    public async Task 保存智能体时写回关联的技能与授权开关()
    {
        var (workspace, vm, _) = await CreateAsync();
        await using var _w = workspace;

        var skill = vm.AvailableSkills.Single(s => s.Name == "执行命令");
        skill.IsSelected = true;
        vm.AllowDangerousTools = true;
        vm.Name = "我的命令助手";

        await vm.SaveCommand.ExecuteAsync(null);

        var services = TestServices.Create(workspace);
        var saved = (await services.Config.GetAgentsAsync()).Single(a => a.Name == "我的命令助手");

        Assert.Contains(SkillService.ShellSkillId, saved.SkillIds);
        Assert.True(saved.AllowDangerousTools);
    }

    [Fact]
    public async Task 勾选危险技能但未授权时给出提示()
    {
        var (workspace, vm, _) = await CreateAsync();
        await using var _w = workspace;

        vm.AllowDangerousTools = false;
        var dangerous = vm.AvailableSkills.Single(s => s.IsDangerous);

        dangerous.IsSelected = true;
        Assert.True(vm.HasUngrantedDangerousSkill, "勾了危险技能却没授权，应当提示");

        vm.AllowDangerousTools = true;
        Assert.False(vm.HasUngrantedDangerousSkill, "授权后提示应消失");
    }

    [Fact]
    public async Task 名称留空时拒绝保存并给出提示()
    {
        var (workspace, vm, _) = await CreateAsync();
        await using var _w = workspace;

        vm.Name = "   ";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.IsStatusError);
        Assert.Contains("名称", vm.StatusMessage);
    }
}
