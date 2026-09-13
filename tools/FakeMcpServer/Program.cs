using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FakeMcpServer;

/// <summary>
/// 最小可用的 MCP stdio 服务端：逐行读取 JSON-RPC，处理后逐行写回。
///
/// 暴露两个工具，用来验证客户端侧的注解映射：
/// <list type="bullet">
///   <item><c>echo</c> 声明了 <c>readOnlyHint</c>，客户端应判为安全</item>
///   <item><c>write_note</c> 未声明任何注解，客户端应判为危险</item>
/// </list>
///
/// 另外提供 resources 与 prompts 能力，用于验证客户端的资源读取与提示展开。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // 用 UTF-8 且不写 BOM：带 BOM 的行首字节会破坏 JSON 解析
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };

        // 设置 FAKE_MCP_LOG=<文件路径> 可把收到的报文记录到文件，排查握手问题时很有用
        var logPath = Environment.GetEnvironmentVariable("FAKE_MCP_LOG");
        void Log(string text)
        {
            if (!string.IsNullOrEmpty(logPath))
            {
                File.AppendAllText(logPath, text + Environment.NewLine);
            }
        }

        while (await stdin.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            Log("收到: " + line);

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonNode? request;
            try
            {
                request = JsonNode.Parse(line);
            }
            catch (JsonException ex)
            {
                await Console.Error.WriteLineAsync($"无法解析请求：{ex.Message}").ConfigureAwait(false);
                continue;
            }

            var id = request?["id"];
            var method = request?["method"]?.GetValue<string>();

            // MCP 的 stdio 关闭流程是"通知 exit、随后关掉 stdin"。这里两种都处理。
            //
            // 实测当前的 C# SDK 在释放传输时两者都没做，只是等满 ShutdownTimeout 再强杀子进程，
            // 因此这段逻辑实际不会被执行到；保留它是为了遵循协议规范——
            // 换成其他客户端（或 SDK 修正了行为）时不必再改这里。
            if (string.Equals(method, "exit", StringComparison.Ordinal))
            {
                Log("收到 exit，退出");
                return 0;
            }

            // 其余通知没有 id，不产生响应
            if (id is null)
            {
                continue;
            }

            var result = method switch
            {
                "initialize" => BuildInitializeResult(),
                "tools/list" => BuildToolsListResult(),
                "tools/call" => BuildToolCallResult(request?["params"]),
                "resources/list" => BuildResourcesListResult(),
                "resources/templates/list" => BuildResourceTemplatesResult(),
                "resources/read" => BuildResourceReadResult(request?["params"]),
                "prompts/list" => BuildPromptsListResult(),
                "prompts/get" => BuildPromptGetResult(request?["params"]),
                "ping" => new JsonObject(),
                "shutdown" => new JsonObject(),
                _ => null,
            };

            var response = result is null
                ? BuildError(id, -32601, $"不支持的方法：{method}")
                : new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id.DeepClone(),
                    ["result"] = result,
                };

            await stdout.WriteLineAsync(response.ToJsonString()).ConfigureAwait(false);
        }

        Log("stdin 已关闭，退出");
        return 0;
    }

    private static JsonObject BuildInitializeResult() => new()
    {
        ["protocolVersion"] = "2024-11-05",
        ["capabilities"] = new JsonObject
        {
            ["tools"] = new JsonObject(),
            ["resources"] = new JsonObject(),
            ["prompts"] = new JsonObject(),
        },
        ["serverInfo"] = new JsonObject { ["name"] = "fake-mcp-server", ["version"] = "1.0.0" },
    };

    private static JsonObject BuildToolsListResult() => new()
    {
        ["tools"] = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "echo",
                ["description"] = "回显传入的文本",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["text"] = new JsonObject { ["type"] = "string", ["description"] = "要回显的文本" },
                    },
                    ["required"] = new JsonArray { "text" },
                },
                // 明确声明只读 —— 客户端应据此判为安全
                ["annotations"] = new JsonObject { ["readOnlyHint"] = true },
            },
            new JsonObject
            {
                ["name"] = "write_note",
                ["description"] = "写一条备注（仅演示，不产生副作用）",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["text"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray { "text" },
                },
                // 刻意不声明 annotations —— 客户端应保守地判为危险
            },
        },
    };

    private static JsonObject BuildToolCallResult(JsonNode? parameters)
    {
        var name = parameters?["name"]?.GetValue<string>();
        var text = parameters?["arguments"]?["text"]?.GetValue<string>() ?? string.Empty;

        var payload = name switch
        {
            "echo" => $"echo: {text}",
            "write_note" => $"已记录备注：{text}",
            _ => null,
        };

        if (payload is null)
        {
            return new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = $"未知工具：{name}" },
                },
                ["isError"] = true,
            };
        }

        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = payload },
            },
            ["isError"] = false,
        };
    }

    // ---------------- resources 与 prompts ----------------

    private const string NoteUri = "file:///notes/readme.md";

    private static JsonObject BuildResourcesListResult() => new()
    {
        ["resources"] = new JsonArray
        {
            new JsonObject
            {
                ["uri"] = NoteUri,
                ["name"] = "readme",
                ["title"] = "项目说明",
                ["description"] = "一段用于验证资源读取的文本",
                ["mimeType"] = "text/markdown",
            },
        },
    };

    private static JsonObject BuildResourceTemplatesResult() => new()
    {
        ["resourceTemplates"] = new JsonArray
        {
            new JsonObject
            {
                ["uriTemplate"] = "file:///notes/{id}",
                ["name"] = "note",
                ["description"] = "按 id 取一条笔记",
                ["mimeType"] = "text/plain",
            },
        },
    };

    private static JsonObject BuildResourceReadResult(JsonNode? parameters)
    {
        var uri = parameters?["uri"]?.GetValue<string>();

        if (!string.Equals(uri, NoteUri, StringComparison.Ordinal))
        {
            return new JsonObject
            {
                ["contents"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["uri"] = uri ?? string.Empty,
                        ["mimeType"] = "text/plain",
                        ["text"] = "（该 URI 没有对应内容）",
                    },
                },
            };
        }

        return new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["uri"] = uri,
                    ["mimeType"] = "text/markdown",
                    ["text"] = "# 项目说明\n\n这是一段用于验证资源读取的正文。",
                },
            },
        };
    }

    private static JsonObject BuildPromptsListResult() => new()
    {
        ["prompts"] = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "summarize",
                ["title"] = "总结",
                ["description"] = "总结给定主题",
                ["arguments"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "topic",
                        ["description"] = "要总结的主题",
                        ["required"] = true,
                    },
                },
            },
        },
    };

    private static JsonObject BuildPromptGetResult(JsonNode? parameters)
    {
        var name = parameters?["name"]?.GetValue<string>();
        var topic = parameters?["arguments"]?["topic"]?.GetValue<string>() ?? "（未指定）";

        if (!string.Equals(name, "summarize", StringComparison.Ordinal))
        {
            return BuildError(
                parameters?["name"] is null ? (JsonNode)JsonValue.Create(0)! : JsonValue.Create(name)!,
                -32602,
                $"未知提示：{name}");
        }

        return new JsonObject
        {
            ["description"] = "总结给定主题",
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = $"请用三句话总结「{topic}」。",
                    },
                },
            },
        };
    }

    private static JsonObject BuildError(JsonNode id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}
