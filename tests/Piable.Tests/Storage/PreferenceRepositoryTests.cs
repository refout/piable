using Piable.Models;
using Piable.Services.Storage;

namespace Piable.Tests.Storage;

public class PreferenceRepositoryTests
{
    [Fact]
    public async Task 从未保存过时返回默认偏好()
    {
        await using var workspace = await TestWorkspace.CreateAsync();

        var prefs = await workspace.Preferences.LoadAsync();

        Assert.True(prefs.ShowStatistics);
        Assert.Equal("System", prefs.Theme);
        Assert.Equal("USD", prefs.Currency);
    }

    [Fact]
    public async Task 保存后可完整读回()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var prefs = new UserPreferences
        {
            ShowStatistics = false,
            ShowCost = false,
            ShowDetailedTokens = true,
            Currency = "CNY",
            ExpandStatisticsByDefault = true,
            LeftPanelCollapsed = true,
            SelectedProviderId = "p1",
            DefaultAgentId = "a1",
            Theme = "Dark",
            MaxToolRounds = 8,
        };

        await workspace.Preferences.SaveAsync(prefs);
        var loaded = await workspace.Preferences.LoadAsync();

        Assert.False(loaded.ShowStatistics);
        Assert.False(loaded.ShowCost);
        Assert.True(loaded.ShowDetailedTokens);
        Assert.Equal("CNY", loaded.Currency);
        Assert.True(loaded.ExpandStatisticsByDefault);
        Assert.True(loaded.LeftPanelCollapsed);
        Assert.Equal("p1", loaded.SelectedProviderId);
        Assert.Equal("a1", loaded.DefaultAgentId);
        Assert.Equal("Dark", loaded.Theme);
        Assert.Equal(8, loaded.MaxToolRounds);
    }

    [Fact]
    public async Task 重复保存只保留一行()
    {
        await using var workspace = await TestWorkspace.CreateAsync();

        await workspace.Preferences.SaveAsync(new UserPreferences { Theme = "Light" });
        await workspace.Preferences.SaveAsync(new UserPreferences { Theme = "Dark" });

        Assert.Equal("Dark", (await workspace.Preferences.LoadAsync()).Theme);

        await using var connection = await workspace.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Preferences;";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task 旧版本缺少新字段时回落到默认值()
    {
        await using var workspace = await TestWorkspace.CreateAsync();

        // 模拟早期版本写入的 JSON：只有当时存在的字段
        await using (var connection = await workspace.Database.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO Preferences (Key, Value, UpdatedAt) VALUES ('user_preferences', $v, $t);";
            command.AddParam("$v", "{\"theme\":\"Dark\"}");
            command.AddParam("$t", DateTimeOffset.Now);
            await command.ExecuteNonQueryAsync();
        }

        var loaded = await workspace.Preferences.LoadAsync();

        Assert.Equal("Dark", loaded.Theme);
        // 未出现的字段应保留属性默认值，而不是变成 false/空串
        Assert.True(loaded.ShowStatistics);
        Assert.True(loaded.ShowCost);
        Assert.Equal("USD", loaded.Currency);
        Assert.Equal(5, loaded.MaxToolRounds);
    }

    [Fact]
    public async Task 偏好数据损坏时不阻断启动()
    {
        await using var workspace = await TestWorkspace.CreateAsync();

        await using (var connection = await workspace.Database.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO Preferences (Key, Value, UpdatedAt) VALUES ('user_preferences', '不是JSON', $t);";
            command.AddParam("$t", DateTimeOffset.Now);
            await command.ExecuteNonQueryAsync();
        }

        var loaded = await workspace.Preferences.LoadAsync();

        Assert.Equal("System", loaded.Theme);
    }
}
