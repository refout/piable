using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Piable.Helpers;
using Piable.Models;

namespace Piable.Services.Tools;

/// <summary>MCP 服务器的连接管理、工具发现与状态查询。</summary>
public interface IMcpClientService : IAsyncDisposable
{
    /// <summary>获取某台服务器提供的工具。连接失败时抛出 <see cref="McpConnectionException"/>。</summary>
    Task<IReadOnlyList<ToolDescriptor>> GetToolsAsync(McpServerConfig config, CancellationToken ct = default);

    /// <summary>
    /// 获取某台服务器的资源与资源模板。
    /// 服务器不支持 resources 能力时返回空列表，而不是抛异常——
    /// 只提供工具的服务器是绝大多数，为"没有资源"让整台服务器不可用毫无道理。
    /// </summary>
    Task<IReadOnlyList<McpResourceDescriptor>> GetResourcesAsync(
        McpServerConfig config, CancellationToken ct = default);

    /// <summary>读取一个资源的内容。</summary>
    Task<McpResourceContent> ReadResourceAsync(
        McpServerConfig config, string uri, CancellationToken ct = default);

    /// <summary>获取某台服务器的提示模板。不支持时返回空列表。</summary>
    Task<IReadOnlyList<McpPromptDescriptor>> GetPromptsAsync(
        McpServerConfig config, CancellationToken ct = default);

    /// <summary>展开一个提示模板，得到它对应的消息序列。</summary>
    Task<IReadOnlyList<McpPromptMessage>> GetPromptAsync(
        McpServerConfig config,
        string promptName,
        IReadOnlyDictionary<string, string?>? arguments = null,
        CancellationToken ct = default);

    /// <summary>断开连接并清除缓存。配置变更后调用，下次使用时重新连接。</summary>
    Task InvalidateAsync(string serverId);

    /// <summary>该服务器当前是否已建立连接。</summary>
    bool IsConnected(string serverId);
}

/// <summary>MCP 服务器连接失败。</summary>
public sealed class McpConnectionException : Exception
{
    public McpConnectionException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <inheritdoc />
public sealed class McpClientService : IMcpClientService
{
    /// <summary>连接建立的超时。给足时间是因为 Stdio 模式下要等 npx/uvx 之类冷启动。</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 断开连接时等待子进程自行退出的上限。
    ///
    /// 注意这是<b>每次必然要等满</b>的时间：SDK 在释放 stdio 传输时既不关闭子进程的
    /// stdin，也不发送关闭通知，只是等待进程退出，因此这段时间是纯粹的等待。
    /// 应用退出时每台服务器都会付出这个代价，故取值偏小——MCP 服务器都是简单子进程，
    /// 2 秒足够它自行收尾，超时后由 SDK 强杀。
    /// </summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<string, ServerConnection> _connections = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private bool _disposed;

    public async Task<IReadOnlyList<ToolDescriptor>> GetToolsAsync(
        McpServerConfig config, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(config);

        var connection = await GetOrConnectAsync(config, ct).ConfigureAwait(false);
        return connection.Tools;
    }

    public async Task<IReadOnlyList<McpResourceDescriptor>> GetResourcesAsync(
        McpServerConfig config, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(config);

        var connection = await GetOrConnectAsync(config, ct).ConfigureAwait(false);
        return connection.Resources;
    }

    public async Task<McpResourceContent> ReadResourceAsync(
        McpServerConfig config, string uri, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(config);

        if (string.IsNullOrWhiteSpace(uri))
        {
            throw new McpConnectionException(Loc.Get("Mcp.NoResourceUri"));
        }

        var connection = await GetOrConnectAsync(config, ct).ConfigureAwait(false);

        try
        {
            var result = await connection.Client
                .ReadResourceAsync(uri, cancellationToken: ct)
                .ConfigureAwait(false);

            return ToContent(uri, result);
        }
        catch (Exception ex) when (ex is not McpConnectionException)
        {
            throw new McpConnectionException(Loc.Get("Mcp.ReadResourceFailed", ex.Message), ex);
        }
    }

    public async Task<IReadOnlyList<McpPromptDescriptor>> GetPromptsAsync(
        McpServerConfig config, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(config);

        var connection = await GetOrConnectAsync(config, ct).ConfigureAwait(false);
        return connection.Prompts;
    }

    public async Task<IReadOnlyList<McpPromptMessage>> GetPromptAsync(
        McpServerConfig config,
        string promptName,
        IReadOnlyDictionary<string, string?>? arguments = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(config);

        var connection = await GetOrConnectAsync(config, ct).ConfigureAwait(false);

        var args = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (arguments is not null)
        {
            foreach (var (key, value) in arguments)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    args[key] = value;
                }
            }
        }

        try
        {
            var result = await connection.Client
                .GetPromptAsync(promptName, args, cancellationToken: ct)
                .ConfigureAwait(false);

            return result.Messages
                .Select(m => new McpPromptMessage
                {
                    // 协议里的角色是小写（"user"/"assistant"），枚举名是 PascalCase，转回小写更符合原义
                    Role = m.Role.ToString().ToLowerInvariant(),

                    // 提示消息的内容块可能是图片、资源等，只有文本块能直接显示
                    Text = m.Content is TextContentBlock text
                        ? text.Text ?? string.Empty
                        : Loc.Get("Mcp.NonTextContent", m.Content.Type),
                })
                .ToList();
        }
        catch (Exception ex) when (ex is not McpConnectionException)
        {
            throw new McpConnectionException(Loc.Get("Mcp.GetPromptFailed", promptName, ex.Message), ex);
        }
    }

    /// <summary>
    /// 把读取结果摊平成本应用的资源内容。
    /// 一个 URI 可能返回多段内容，这里按段拼接；二进制段给出说明而非 base64。
    /// </summary>
    private static McpResourceContent ToContent(string uri, ReadResourceResult result)
    {
        var builder = new System.Text.StringBuilder();
        var isBinary = false;

        foreach (var item in result.Contents)
        {
            if (item is TextResourceContents text)
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.Append(text.Text);
                continue;
            }

            isBinary = true;
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(Loc.Get("Mcp.BinaryOmitted", item.MimeType ?? Loc.Get("Common.Unknown")));
        }

        return new McpResourceContent
        {
            Uri = uri,
            MimeType = result.Contents.Count > 0 ? result.Contents[0].MimeType : null,
            Text = builder.Length == 0 ? Loc.Get("Mcp.EmptyResource") : builder.ToString(),
            IsBinary = isBinary,
        };
    }

    public async Task InvalidateAsync(string serverId)
    {
        if (_connections.TryRemove(serverId, out var connection))
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    public bool IsConnected(string serverId) =>
        _connections.TryGetValue(serverId, out var connection) && connection.IsAlive;

    public async ValueTask DisposeAsync()
    {
        _disposed = true;

        var connections = _connections.Values.ToList();
        _connections.Clear();

        // 并行释放：每个连接的关闭都要等满 ShutdownTimeout（SDK 不会主动通知子进程退出），
        // 顺序释放会让退出耗时随服务器数量线性增长，并行则恒定为一个超时周期。
        await Task.WhenAll(connections.Select(c => c.DisposeAsync().AsTask())).ConfigureAwait(false);

        _connectGate.Dispose();
    }

    /// <summary>
    /// 取已有连接，没有就建一个。
    ///
    /// 连接期间持有全局信号量：MCP 的握手可能耗时数秒（Stdio 要等子进程起来），
    /// 让并发请求同时对同一台服务器发起握手既浪费又可能触发服务端的连接数限制。
    /// 信号量只保护"建立连接"这一步，已连上的服务器取工具不受影响。
    /// </summary>
    private async Task<ServerConnection> GetOrConnectAsync(McpServerConfig config, CancellationToken ct)
    {
        if (_connections.TryGetValue(config.Id, out var existing) && existing.IsAlive)
        {
            return existing;
        }

        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 双重检查：等锁期间可能已有别的调用把它连上了
            if (_connections.TryGetValue(config.Id, out existing) && existing.IsAlive)
            {
                return existing;
            }

            if (existing is not null)
            {
                _connections.TryRemove(config.Id, out _);
                await existing.DisposeAsync().ConfigureAwait(false);
            }

            var connection = await ConnectAsync(config).ConfigureAwait(false);
            _connections[config.Id] = connection;
            return connection;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private static async Task<ServerConnection> ConnectAsync(McpServerConfig config)
    {
        // 刻意不使用调用方的 CancellationToken：首个调用者取消不应连带让
        // 其他等待同一台服务器的调用一起失败。超时由 ConnectTimeout 单独控制。
        using var timeoutCts = new CancellationTokenSource(ConnectTimeout);

        McpClient? client = null;
        try
        {
            var transport = CreateTransport(config);
            var options = new McpClientOptions
            {
                ClientInfo = new() { Name = "Piable", Version = "1.0.0" },
                InitializationTimeout = ConnectTimeout,
            };

            client = await McpClient
                .CreateAsync(transport, options, NullLoggerFactory.Instance, timeoutCts.Token)
                .ConfigureAwait(false);

            var protocolTools = await client
                .ListToolsAsync(cancellationToken: timeoutCts.Token)
                .ConfigureAwait(false);

            var prefix = ToolNaming.Suggest(config.Name, "mcp");
            var tools = protocolTools
                .Select(tool => ToDescriptor(tool, config, prefix))
                .ToList();

            // 资源与提示是可选能力：连不上、没实现、报个错都不该影响工具的使用，
            // 因此这里只尽力而为，失败就当作这台服务器没有提供。
            var resources = await TryListResourcesAsync(client, config, timeoutCts.Token)
                .ConfigureAwait(false);
            var prompts = await TryListPromptsAsync(client, config, timeoutCts.Token)
                .ConfigureAwait(false);

            return new ServerConnection(client, tools, resources, prompts);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            await SafeDisposeAsync(client).ConfigureAwait(false);
            throw new McpConnectionException(
                Loc.Get("Mcp.ConnectTimeout", config.Name, ConnectTimeout.TotalSeconds));
        }
        catch (Exception ex)
        {
            await SafeDisposeAsync(client).ConfigureAwait(false);
            throw new McpConnectionException(
                Loc.Get("Mcp.ConnectFailedDetail", config.Name, ex.Message), ex);
        }
    }

    private static async Task<List<McpResourceDescriptor>> TryListResourcesAsync(
        McpClient client, McpServerConfig config, CancellationToken ct)
    {
        var result = new List<McpResourceDescriptor>();

        try
        {
            foreach (var resource in await client.ListResourcesAsync(cancellationToken: ct)
                         .ConfigureAwait(false))
            {
                result.Add(new McpResourceDescriptor
                {
                    ServerId = config.Id,
                    ServerName = config.Name,
                    Uri = resource.Uri,
                    Name = resource.Name,
                    Title = resource.Title,
                    Description = resource.Description,
                    MimeType = resource.MimeType,
                });
            }
        }
        catch (Exception)
        {
            // 服务器未声明 resources 能力，或实现有瑕疵
        }

        try
        {
            foreach (var template in await client.ListResourceTemplatesAsync(cancellationToken: ct)
                         .ConfigureAwait(false))
            {
                result.Add(new McpResourceDescriptor
                {
                    ServerId = config.Id,
                    ServerName = config.Name,
                    Uri = template.UriTemplate,
                    Name = template.Name,
                    Title = template.Title,
                    Description = template.Description,
                    MimeType = template.MimeType,
                    IsTemplate = true,
                });
            }
        }
        catch (Exception)
        {
            // 同上
        }

        return result;
    }

    private static async Task<List<McpPromptDescriptor>> TryListPromptsAsync(
        McpClient client, McpServerConfig config, CancellationToken ct)
    {
        var result = new List<McpPromptDescriptor>();

        try
        {
            foreach (var prompt in await client.ListPromptsAsync(cancellationToken: ct)
                         .ConfigureAwait(false))
            {
                result.Add(new McpPromptDescriptor
                {
                    ServerId = config.Id,
                    ServerName = config.Name,
                    Name = prompt.Name,
                    Title = prompt.Title,
                    Description = prompt.Description,
                    Arguments = (prompt.ProtocolPrompt.Arguments ?? [])
                        .Select(a => new McpPromptArgumentDescriptor
                        {
                            Name = a.Name,
                            Description = a.Description,
                            Required = a.Required == true,
                        })
                        .ToList(),
                });
            }
        }
        catch (Exception)
        {
            // 服务器未声明 prompts 能力
        }

        return result;
    }

    /// <summary>
    /// MCP 工具名会加上服务器前缀。不同服务器完全可能提供同名工具
    /// （比如都叫 <c>search</c>），不加前缀时后注册的会把先注册的顶掉。
    /// </summary>
    private static ToolDescriptor ToDescriptor(McpClientTool tool, McpServerConfig config, string prefix)
    {
        var namespaced = ToolNaming.Suggest($"{prefix}_{tool.Name}", tool.Name);
        var exposed = tool.WithName(namespaced);
        var readOnly = tool.ProtocolTool.Annotations?.ReadOnlyHint == true;

        return new ToolDescriptor
        {
            Tool = exposed,
            Name = namespaced,
            Source = ToolSource.Mcp,
            // 只有服务端明确声明了只读才按安全对待；未声明的一律按危险处理——
            // 第三方服务器的工具能做什么，客户端无从验证。
            Risk = readOnly ? ToolRisk.Safe : ToolRisk.Dangerous,
            SourceLabel = $"MCP · {config.Name}",
            Description = tool.Description,
            OriginalName = tool.Name,
        };
    }

    private static IClientTransport CreateTransport(McpServerConfig config) => config.Transport switch
    {
        McpTransport.Stdio => new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = config.Name,
                Command = string.IsNullOrWhiteSpace(config.Command)
                    ? throw new McpConnectionException(Loc.Get("Mcp.NoCommand", config.Name))
                    : config.Command,
                Arguments = [.. config.Args],
                ShutdownTimeout = ShutdownTimeout,
            },
            NullLoggerFactory.Instance),

        McpTransport.Sse => CreateHttpTransport(config, HttpTransportMode.Sse),

        _ => CreateHttpTransport(config, HttpTransportMode.StreamableHttp),
    };

    private static IClientTransport CreateHttpTransport(McpServerConfig config, HttpTransportMode mode)
    {
        if (string.IsNullOrWhiteSpace(config.Url) || !Uri.TryCreate(config.Url, UriKind.Absolute, out var endpoint))
        {
            throw new McpConnectionException(Loc.Get("Mcp.InvalidUrl", config.Name, config.Url));
        }

        return new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Name = config.Name,
                Endpoint = endpoint,
                TransportMode = mode,
                AdditionalHeaders = new Dictionary<string, string>(config.Headers, StringComparer.Ordinal),
                ConnectionTimeout = ConnectTimeout,
            },
            NullLoggerFactory.Instance);
    }

    private static async Task SafeDisposeAsync(McpClient? client)
    {
        if (client is null)
        {
            return;
        }

        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 握手失败后的清理，异常没有处理价值
        }
    }

    /// <summary>一台已连接服务器及其工具、资源、提示清单。</summary>
    private sealed class ServerConnection(
        McpClient client,
        IReadOnlyList<ToolDescriptor> tools,
        IReadOnlyList<McpResourceDescriptor> resources,
        IReadOnlyList<McpPromptDescriptor> prompts)
        : IAsyncDisposable
    {
        public McpClient Client { get; } = client;

        public IReadOnlyList<ToolDescriptor> Tools { get; } = tools;

        public IReadOnlyList<McpResourceDescriptor> Resources { get; } = resources;

        public IReadOnlyList<McpPromptDescriptor> Prompts { get; } = prompts;

        public bool IsAlive => !Client.Completion.IsCompleted;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 关闭时的异常不影响调用方
            }
        }
    }
}
