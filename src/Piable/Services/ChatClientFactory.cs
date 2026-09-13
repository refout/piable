using System.ClientModel;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI;
using Piable.Helpers;
using Piable.Models;

namespace Piable.Services;

/// <summary>按供应商配置构造 <see cref="IChatClient"/>。</summary>
public interface IChatClientFactory
{
    /// <summary>为指定供应商与模型创建客户端。调用方负责释放。</summary>
    IChatClient Create(ProviderConfig provider, string model);

    /// <summary>解析本次实际使用的模型名：智能体指定优先，其次供应商默认。</summary>
    string ResolveModel(ProviderConfig provider, Agent? agent);

    /// <summary>解析最终生效的 Endpoint：用户覆盖优先，其次预设默认值。</summary>
    string ResolveEndpoint(ProviderConfig provider);
}

/// <inheritdoc />
public sealed class ChatClientFactory : IChatClientFactory
{
    public IChatClient Create(ProviderConfig provider, string model)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var preset = ProviderPresets.Find(provider.PresetId);
        var endpoint = ResolveEndpoint(provider);

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException(Loc.Get("Provider.NoEndpoint"));
        }

        return preset?.ProviderType == ProviderType.AzureOpenAI
            ? CreateAzure(provider, endpoint, model)
            : CreateOpenAiCompatible(provider, endpoint, model);
    }

    public string ResolveModel(ProviderConfig provider, Agent? agent)
    {
        if (!string.IsNullOrWhiteSpace(agent?.Model))
        {
            return agent.Model;
        }

        // Azure 走部署名，没有部署名时退回到模型名（多数情况下两者一致或用户只填其一）
        if (string.IsNullOrWhiteSpace(provider.DefaultModel)
            && !string.IsNullOrWhiteSpace(provider.DeploymentName))
        {
            return provider.DeploymentName;
        }

        return provider.DefaultModel;
    }

    public string ResolveEndpoint(ProviderConfig provider)
    {
        if (!string.IsNullOrWhiteSpace(provider.Endpoint))
        {
            return provider.Endpoint;
        }

        return ProviderPresets.Find(provider.PresetId)?.DefaultEndpoint ?? string.Empty;
    }

    private static IChatClient CreateOpenAiCompatible(
        ProviderConfig provider, string endpoint, string model)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(NormalizeEndpoint(endpoint)),
        };

        // 本地部署的 Ollama 等无需鉴权，但 OpenAI SDK 要求凭据非空，
        // 填一个占位值即可——服务器不会校验它。
        var credential = new ApiKeyCredential(
            string.IsNullOrWhiteSpace(provider.ApiKey) ? "piable-no-key-required" : provider.ApiKey);

        var client = new OpenAIClient(credential, options);
        return client.GetChatClient(model).AsIChatClient();
    }

    private static IChatClient CreateAzure(ProviderConfig provider, string endpoint, string model)
    {
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            throw new InvalidOperationException(Loc.Get("Provider.AzureKeyRequired"));
        }

        // Azure 以部署名寻址；用户没单独填部署名时按模型名处理
        var deployment = string.IsNullOrWhiteSpace(provider.DeploymentName)
            ? model
            : provider.DeploymentName;

        var client = new AzureOpenAIClient(new Uri(endpoint), new ApiKeyCredential(provider.ApiKey));
        return client.GetChatClient(deployment).AsIChatClient();
    }

    /// <summary>OpenAI SDK 要求 Endpoint 是绝对 URI 且以 / 结尾，否则路径会被截断。</summary>
    private static string NormalizeEndpoint(string endpoint)
    {
        var trimmed = endpoint.Trim();
        return trimmed.EndsWith('/') ? trimmed : trimmed + "/";
    }
}
