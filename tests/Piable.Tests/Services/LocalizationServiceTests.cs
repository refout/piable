using System.Text.RegularExpressions;
using Piable.Helpers;
using Piable.Services;
using Piable.Tests.ViewModels;
using Piable.ViewModels;

namespace Piable.Tests.Services;

/// <summary>
/// 本地化的三件事：语言包打得进程序、两套包的条目对得上、切语言后界面会跟着变。
/// </summary>
public sealed class LocalizationServiceTests
{
    [Fact]
    public void 语言包已作为嵌入资源打包()
    {
        var names = typeof(LocalizationService).Assembly.GetManifestResourceNames();

        Assert.Contains("Piable.Resources.Strings.zh-CN.json", names);
        Assert.Contains("Piable.Resources.Strings.en-US.json", names);
    }

    [Fact]
    public void 两套语言包的条目一一对应()
    {
        var zh = new LocalizationService(LocalizationService.DefaultLanguage);
        var en = new LocalizationService(LocalizationService.English);

        Assert.Equal(zh.Strings.Count, en.Strings.Count);
        Assert.Equal(zh.Strings.Keys.OrderBy(k => k, StringComparer.Ordinal),
            en.Strings.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void 带占位符的条目在两套语言包里占位符一致()
    {
        var zh = new LocalizationService(LocalizationService.DefaultLanguage);
        var en = new LocalizationService(LocalizationService.English);

        var pattern = new Regex(@"\{(\d+)(:[^}]*)?\}");
        var mismatched = new List<string>();

        foreach (var key in zh.Strings.Keys)
        {
            var zhPlaceholders = pattern.Matches(zh.Strings[key]).Select(m => m.Groups[1].Value);
            var enPlaceholders = pattern.Matches(en.Strings[key]).Select(m => m.Groups[1].Value);

            if (!zhPlaceholders.SequenceEqual(enPlaceholders))
            {
                mismatched.Add(key);
            }
        }

        Assert.Empty(mismatched);
    }

    [Fact]
    public void 代码与界面用到的键都在语言包里()
    {
        var root = FindRepositoryRoot();
        Assert.NotNull(root);

        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(root, "src", "Piable"), "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"Loc\.Get\(\s*""([^""]+)"""))
            {
                used.Add(match.Groups[1].Value);
            }
        }

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(root, "src", "Piable"), "*.axaml", SearchOption.AllDirectories))
        {
            foreach (Match match in Regex.Matches(
                         File.ReadAllText(file), @"\{DynamicResource Loc\.([A-Za-z0-9_.]+)\}"))
            {
                used.Add(match.Groups[1].Value);
            }
        }

        // 至少得是个像样的规模，否则说明扫描路径错了——那这个测试就成了摆设
        Assert.True(used.Count > 200, $"只扫到 {used.Count} 个键，扫描路径可能不对");

        var zh = new LocalizationService(LocalizationService.DefaultLanguage);
        var en = new LocalizationService(LocalizationService.English);

        Assert.DoesNotContain(used, k => !zh.Strings.ContainsKey(k) || !en.Strings.ContainsKey(k));
    }

    [Fact]
    public void 切换到英文后取到的是英文()
    {
        var service = new LocalizationService();

        Assert.Equal(LocalizationService.DefaultLanguage, service.LanguageCode);
        var chinese = service.Get("Pref.Theme");
        Assert.Equal("主题", chinese);

        service.SetLanguage(LocalizationService.English);

        Assert.Equal(LocalizationService.English, service.LanguageCode);
        Assert.Equal("Theme", service.Get("Pref.Theme"));
        Assert.NotEqual(chinese, service.Get("Pref.Theme"));
    }

    [Fact]
    public void 切换语言会通知界面重取文案()
    {
        var service = new LocalizationService();
        var fired = 0;
        service.LanguageChanged += (_, _) => fired++;

        service.SetLanguage(LocalizationService.English);
        Assert.Equal(1, fired);

        // 重复设成同一门语言不该再打扰界面
        service.SetLanguage(LocalizationService.English);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void 未知的语言代码退回默认语言()
    {
        var service = new LocalizationService("klingon");

        Assert.Equal(LocalizationService.DefaultLanguage, service.LanguageCode);
        Assert.Equal("主题", service.Get("Pref.Theme"));
    }

    [Fact]
    public void 缺失的键原样返回键名而不是空白()
    {
        var service = new LocalizationService();

        // 界面上露出 Loc.Xxx 比一片空白好排查
        Assert.Equal("Loc.NoSuchKey", service.Get("Loc.NoSuchKey"));
    }

    [Fact]
    public void 带占位符的文案按当前语言格式化()
    {
        var service = new LocalizationService(LocalizationService.English);

        Assert.Equal("Following default: gpt-4o", service.Get("Chat.FollowDefaultHint", "gpt-4o"));
    }

    [Fact]
    public async Task 偏好页选择语言后落库并切换服务()
    {
        var workspace = await TestWorkspace.CreateAsync();
        await using var _ = workspace;

        var services = TestServices.Create(workspace);
        await services.InitializeSeedDataAsync();

        // 注入独立实例：直接用 Loc.Service 会改掉整个进程的默认语言，干扰别的测试
        var localization = new LocalizationService(LocalizationService.DefaultLanguage);
        var viewModel = new PreferencesViewModel(services.Config, new StubStatusReporter(), localization);
        await viewModel.LoadAsync();

        Assert.Equal(LocalizationService.DefaultLanguage, localization.LanguageCode);
        Assert.Equal(LocalizationService.DefaultLanguage, viewModel.SelectedLanguage!.Code);

        viewModel.SelectedLanguage = viewModel.AvailableLanguages
            .First(l => l.Code == LocalizationService.English);

        Assert.Equal(LocalizationService.English, localization.LanguageCode);
        await WaitForAsync(() => services.Config.GetPreferencesAsync().Result.Language
                                 == LocalizationService.English);
    }

    [Fact]
    public async Task 偏好页读回的语言代码不存在时落在第一项上()
    {
        var workspace = await TestWorkspace.CreateAsync();
        await using var _ = workspace;

        var services = TestServices.Create(workspace);
        await services.InitializeSeedDataAsync();

        var preferences = await services.Config.GetPreferencesAsync();
        preferences.Language = "klingon";
        await services.Config.SavePreferencesAsync(preferences);

        var viewModel = new PreferencesViewModel(services.Config, new StubStatusReporter(),
            new LocalizationService(LocalizationService.DefaultLanguage));
        await viewModel.LoadAsync();

        Assert.NotNull(viewModel.SelectedLanguage);
        Assert.Equal(LocalizationService.DefaultLanguage, viewModel.SelectedLanguage!.Code);
    }

    [Fact]
    public async Task 切换语言后视图模型的派生文本跟着重算()
    {
        var workspace = await TestWorkspace.CreateAsync();
        await using var _ = workspace;

        var services = TestServices.Create(workspace);
        await services.InitializeSeedDataAsync();

        var viewModel = services.CreateMainWindowViewModel();
        await viewModel.InitializeAsync();

        var session = viewModel.CurrentSession;
        Assert.NotNull(session);

        var chinese = session.ModelFollowDefaultHint;
        Assert.Contains("跟随默认", chinese);

        // 派生文本不在资源字典里，靠的是"所有属性都变了"这一次通知。
        // 门面是静态的，用完必须换回去，否则同进程里别的测试会读到英文。
        var original = Loc.Service;
        try
        {
            var service = new LocalizationService(LocalizationService.DefaultLanguage);
            Loc.Use(service);
            service.SetLanguage(LocalizationService.English);

            Assert.Contains("Following default", session.ModelFollowDefaultHint);
        }
        finally
        {
            Loc.Use(original);
        }

        Assert.Equal(chinese, session.ModelFollowDefaultHint);
    }

    /// <summary>从测试输出目录往上找到仓库根（含 Piable.slnx 的那一层）。</summary>
    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (directory.EnumerateFiles("Piable.slnx").Any()
                || directory.EnumerateFiles("Piable.sln").Any())
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }
    }
}
