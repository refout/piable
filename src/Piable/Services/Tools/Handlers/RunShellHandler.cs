using System.Diagnostics;
using System.Text;
using Piable.Helpers;
using Piable.Models;

namespace Piable.Services.Tools.Handlers;

/// <summary>
/// 在本机执行 shell 命令，返回退出码与输出。
///
/// <b>这是本程序里权限最高的操作</b>：命令以当前用户身份运行，能读写任意文件、
/// 发起网络请求、启动其他进程。风险不在于工具本身，而在于模型可能被对话内容或
/// 其他工具的返回值里的提示注入诱导着去调用它，且执行结果无法撤销。
/// 因此它被标记为 <see cref="ToolRisk.Dangerous"/>，
/// 只有智能体显式开启「允许执行危险工具」后才会真正执行。
/// </summary>
public sealed class RunShellHandler : ISkillHandler
{
    /// <summary>输出上限。超出部分截断，避免一次命令把上下文窗口撑爆。</summary>
    private const int MaxOutputChars = 8000;

    private const int DefaultTimeoutSeconds = 30;
    private const int MaxTimeoutSeconds = 300;

    public string Key => "run_shell";

    public string DisplayName => Loc.Get("Skill.RunShell.Name");

    public ToolRisk Risk => ToolRisk.Dangerous;

    public string SuggestedDescription => Loc.Get("Skill.RunShell.Description");

    // 用 $$ 让 raw string 里的 {{ }} 成为插值占位：JSON 的花括号不再需要转义
    public string SuggestedToolSpec => $$"""
        {
          "type": "object",
          "properties": {
            "command": {
              "type": "string",
              "description": "{{Loc.Get("Skill.RunShell.SpecCommand")}}"
            },
            "timeout_seconds": {
              "type": "integer",
              "description": "{{Loc.Get("Skill.RunShell.SpecTimeout")}}"
            }
          },
          "required": ["command"]
        }
        """;

    public async Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        var command = ReadString(arguments, "command");
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new SkillExecutionException(Loc.Get("Skill.RunShell.MissingCommand"));
        }

        var timeout = TimeSpan.FromSeconds(
            Math.Clamp(ReadInt(arguments, "timeout_seconds") ?? DefaultTimeoutSeconds, 1, MaxTimeoutSeconds));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("/c");
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
        }

        startInfo.ArgumentList.Add(command);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new SkillExecutionException(Loc.Get("Skill.RunShell.StartFailed", ex.Message), ex);
        }

        // 必须并发读取两个流：只读其一会让另一个的缓冲区写满，进程随之阻塞
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户主动取消是控制流而非"技能执行失败"，必须原样向上传播：
            // 包装成 SkillExecutionException 会被当成工具错误回填给模型，
            // 于是对话在用户已经按下停止之后还继续跑下去。
            KillQuietly(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            throw new SkillExecutionException(
                Loc.Get("Skill.RunShell.Timeout", timeout.TotalSeconds));
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        return Format(process.ExitCode, stdout, stderr);
    }

    /// <summary>终止进程及其派生的子进程。子进程往往才是真正占住管道的那个。</summary>
    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // 进程可能刚好自己退出了，或权限不足以结束进程树
        }
    }

    private static string Format(int exitCode, string stdout, string stderr)
    {
        var builder = new StringBuilder();
        builder.Append(Loc.Get("Skill.RunShell.ExitCode")).Append(exitCode).AppendLine();

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            builder.AppendLine(Loc.Get("Skill.RunShell.Stdout"));
            builder.AppendLine(Truncate(stdout));
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            builder.AppendLine(Loc.Get("Skill.RunShell.Stderr"));
            builder.AppendLine(Truncate(stderr));
        }

        if (string.IsNullOrWhiteSpace(stdout) && string.IsNullOrWhiteSpace(stderr))
        {
            builder.AppendLine(Loc.Get("Skill.RunShell.NoOutput"));
        }

        return builder.ToString().TrimEnd();
    }

    private static string Truncate(string text)
    {
        var trimmed = text.TrimEnd();
        return trimmed.Length <= MaxOutputChars
            ? trimmed
            : trimmed[..MaxOutputChars] + Loc.Get("Skill.RunShell.Truncated", trimmed.Length - MaxOutputChars);
    }

    /// <summary>
    /// 从模型给的参数里取字符串。模型偶尔会把数字或布尔写成字符串，
    /// 这里做一次宽松转换，不因为类型细节让一次有用的调用白白失败。
    /// </summary>
    private static string? ReadString(IReadOnlyDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string s => s,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } e => e.GetString(),
            System.Text.Json.JsonElement e => e.ToString(),
            _ => value.ToString(),
        };
    }

    private static int? ReadInt(IReadOnlyDictionary<string, object?> arguments, string key)
    {
        var raw = ReadString(arguments, key);
        return int.TryParse(raw, out var value) ? value : null;
    }
}
