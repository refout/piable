# LLM 对话应用程序 —— 完整设计文档（v3.0 最终版）

**项目名称：** LLMChatApp  
**文档版本：** 3.0  
**最后更新：** 2026-09-07  
**文档状态：** 最终版，可直接指导开发

---

## 目录

1. [项目概述](#一项目概述)
2. [技术选型与架构](#二技术选型与架构)
3. [界面布局设计](#三界面布局设计)
4. [核心交互流程](#四核心交互流程)
5. [数据模型设计](#五数据模型设计)
6. [数据存储设计](#六数据存储设计)
7. [API 集成设计](#七api-集成设计)
8. [智能体管理与工具调用](#八智能体管理与工具调用)
9. [Token 统计与费用计算](#九token-统计与费用计算)
10. [异常处理与用户体验](#十异常处理与用户体验)
11. [性能优化与 AOT 兼容](#十一性能优化与-aot-兼容)
12. [项目文件结构](#十二项目文件结构)
13. [构建与发布](#十三构建与发布)
14. [路线图与扩展方向](#十四路线图与扩展方向)
15. [附录](#十五附录)

---

## 一、项目概述

### 1.1 项目目标

开发一款基于 **Avalonia UI** 的跨平台桌面 LLM 对话应用，核心目标：

| 目标 | 说明 |
| :--- | :--- |
| **跨平台支持** | 同时支持 Windows、macOS、Linux |
| **流式对话** | 支持大模型流式输出，实时显示生成内容 |
| **内置供应商** | 预设主流 API 供应商，用户只需填写 API Key 即可使用 |
| **智能体** | 创建多个智能体，每个智能体包含系统提示词、MCP 服务器、技能等配置 |
| **模型动态获取** | 支持从供应商 API 动态获取模型列表，也可手动添加 |
| **对话管理** | 完整的历史对话管理（新建、切换、删除、重命名） |
| **统计透明** | 显示每次对话的耗时、Token 消耗和费用估算 |
| **原生体验** | 单窗口设计，删除确认采用内联方式，不使用弹窗 |

### 1.2 设计原则

| 原则 | 说明 |
| :--- | :--- |
| **单窗口设计** | 所有功能集成在单个主窗口中，删除确认采用内联方式 |
| **智能体驱动** | 所有对话均通过“智能体”进行，智能体可配置系统提示词、工具等 |
| **流式优先** | 所有 AI 响应采用流式输出，逐字显示 |
| **状态持久化** | 对话历史、配置信息自动保存到本地 |
| **AOT 兼容** | 优先使用源生成器，避免运行时反射，并设验证与备选方案 |

---

## 二、技术选型与架构

### 2.1 技术栈总览

| 类别 | 技术方案 | 版本 | 说明 |
| :--- | :--- | :--- | :--- |
| **UI框架** | Avalonia UI | 11.x | 跨平台桌面应用框架 |
| **架构模式** | MVVM | - | 视图与业务逻辑分离 |
| **开发语言** | C# | 13 (.NET 10) | 最新语言特性 |
| **编译模式** | Native AOT（优先）或 SingleFile | - | AOT 验证通过则采用，否则回退 |
| **AI抽象层** | Microsoft.Extensions.AI | 最新 | 微软官方 AI 接口统一层 |
| **OpenAI适配** | Microsoft.Extensions.AI.OpenAI | 最新 | OpenAI/Azure 适配器 |
| **依赖注入** | Microsoft.Extensions.DependencyInjection | 最新 | 服务生命周期管理 |
| **JSON序列化** | System.Text.Json | 最新 | 源生成器模式 |
| **数据存储** | SQLite (Microsoft.Data.Sqlite) | 最新 | 轻量级数据库 |
| **Markdown渲染** | LiveMarkdown.Avalonia（验证后确认） | 最新 | 流式渲染支持，若 AOT 不兼容则替换 |
| **加密** | Microsoft.AspNetCore.DataProtection | 最新 | 跨平台密钥保护 |

**AOT 兼容性验证计划：**  
项目启动前（Sprint 0）搭建最小原型，验证上述依赖在 Native AOT 下的可行性。若 AOT 编译失败，则放弃 AOT，改用 `PublishSingleFile` 方式发布。

### 2.2 整体架构图

```
┌─────────────────────────────────────────────────────────────────────┐
│                         UI 层 (Views)                             │
│              MainWindow / 用户控件 / 样式 / 资源                   │
└─────────────────────────────────┬───────────────────────────────────┘
                                  │ 数据绑定 / 命令
┌─────────────────────────────────▼───────────────────────────────────┐
│                       ViewModel 层                                 │
│  ┌───────────────────────────────────────────────────────────────┐ │
│  │ MainWindowViewModel (主窗口)                                  │ │
│  │   ├─ 对话列表管理                                            │ │
│  │   ├─ 视图切换控制                                            │ │
│  │   ├─ 配置管理入口                                            │ │
│  │   └─ 当前会话管理                                            │ │
│  └───────────────────────────────────────────────────────────────┘ │
│  ┌───────────────────────────────────────────────────────────────┐ │
│  │ ChatSessionViewModel (单个对话)                               │ │
│  │   ├─ 消息列表管理                                            │ │
│  │   ├─ 流式对话逻辑（通过编排器）                              │ │
│  │   ├─ Token统计与费用计算                                     │ │
│  │   └─ 中断控制                                                │ │
│  └───────────────────────────────────────────────────────────────┘ │
│  ┌───────────────────────────────────────────────────────────────┐ │
│  │ MessageViewModel (单条消息)                                   │ │
│  └───────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────┬───────────────────────────────────┘
                                  │ 调用服务
┌─────────────────────────────────▼───────────────────────────────────┐
│                        服务层 (Services)                           │
│  ┌───────────────────────────────────────────────────────────────┐ │
│  │ IAgentOrchestrator (对话编排)                                 │ │
│  │   ├─ 管理消息历史与工具调用循环                              │ │
│  │   ├─ 调用 IChatClient 进行流式生成                           │ │
│  │   └─ 处理工具调用（MCP/Skill）                               │ │
│  ├───────────────────────────────────────────────────────────────┤ │
│  │ IChatClientFactory                                           │ │
│  │   └─ 根据供应商配置创建 IChatClient                          │ │
│  ├───────────────────────────────────────────────────────────────┤ │
│  │ IConfigService                                               │ │
│  │   ├─ 供应商配置 CRUD                                         │ │
│  │   ├─ 智能体 CRUD                                             │ │
│  │   ├─ MCP 服务器 CRUD                                         │ │
│  │   ├─ 技能 CRUD                                               │ │
│  │   └─ 用户偏好管理                                            │ │
│  ├───────────────────────────────────────────────────────────────┤ │
│  │ ISessionService                                              │ │
│  │   ├─ 对话会话 CRUD                                           │ │
│  │   ├─ 消息管理                                                │ │
│  │   └─ 数据备份/恢复                                           │ │
│  ├───────────────────────────────────────────────────────────────┤ │
│  │ ITokenCostCalculator                                         │ │
│  │   ├─ Token 统计                                              │ │
│  │   ├─ 费用估算                                                │ │
│  │   └─ 模型定价管理                                            │ │
│  ├───────────────────────────────────────────────────────────────┤ │
│  │ IModelListService                                            │ │
│  │   └─ 从供应商 API 获取模型列表                               │ │
│  ├───────────────────────────────────────────────────────────────┤ │
│  │ IMcpClientService                                            │ │
│  │   ├─ 管理与 MCP 服务器的连接                                │ │
│  │   ├─ 工具发现                                                │ │
│  │   └─ 工具执行                                                │ │
│  ├───────────────────────────────────────────────────────────────┤ │
│  │ ISkillService                                                │ │
│  │   ├─ 技能 CRUD                                               │ │
│  │   └─ 本地技能执行                                            │ │
│  └───────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────┬───────────────────────────────────┘
                                  │ 数据持久化
┌─────────────────────────────────▼───────────────────────────────────┐
│                        数据存储层                                   │
│  ┌───────────────────────────────────────────────────────────────┐ │
│  │ SQLite 数据库 (llmchat.db)                                   │ │
│  │   ├─ Providers 表                                            │ │
│  │   ├─ Agents 表                                               │ │
│  │   ├─ McpServers 表                                           │ │
│  │   ├─ Skills 表                                               │ │
│  │   ├─ Sessions 表                                             │ │
│  │   ├─ Messages 表                                             │ │
│  │   └─ Preferences 表 (用户偏好)                               │ │
│  └───────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────────┘
```

---

## 三、界面布局设计

### 3.1 整体布局结构

#### 模式：对话视图（默认）
```
+=======================================================================+
| [≡]                         AI 助手                                    |
+----------------------------+==========================================+
|                            |  对话标题栏                               |
|  📝 对话历史              |  "关于AI的讨论"  💬12条·🔢2.3K·⏱8.7s    |
|  ├─ 对话1                 |  ─────────────────────────────────────────│
|  ├─ 对话2 (当前) ★        |  消息列表                                 |
|  ├─ 对话3                 |  [用户] 你好                             |
|  ├─ 对话4                 |  [AI] 你好！我是AI助手...                |
|  ├─ ...                   |  (Markdown流式渲染)                      |
|                            |                                          |
|  [✨ 新对话]               |  ─────────────────────────────────────────│
|                            |  输入区域                                 |
|  [⚙ 配置]                 |  [智能体选择器] [⚙参数折叠] [● 已连接]  |
|                            |  [Temp滑块] [MaxToken滑块] [TopP滑块]   |
|                            |  [多行输入框]              [发送/停止]    |
+----------------------------+==========================================+
```

#### 模式：配置视图
配置视图使用 **TabControl** 分为三个标签页：`API 配置`、`智能体管理`、`偏好设置`。

**API 配置标签页：**
```
┌───────────────────────────────────────────────────────────────────────┐
│  选择供应商: [OpenAI ▼]  (内置预设列表)                               │
│  ──────────────────────────────────────────────────────────────────── │
│  API Key: [sk-...] [显示]                                            │
│  Endpoint: [https://api.openai.com/v1] (可编辑)                      │
│  模型列表: [gpt-4o] [✕] [gpt-4o-mini] [✕] [+ 添加模型] [获取模型列表] │
│  默认模型: [gpt-4o-mini ▼]                                           │
│  默认参数: Temperature [0.7]  MaxTokens [2048]  TopP [1.0]           │
│  模型定价: 输入 [0.005] $/1K  输出 [0.015] $/1K  [常用预设 ▼]         │
│  [设为默认供应商]  [保存配置]  [测试连接]                            │
│  ✅ 配置已保存                                                       │
└───────────────────────────────────────────────────────────────────────┘
```

**智能体管理标签页：**
```
┌───────────────────────────────────────────────────────────────────────┐
│  📋 我的智能体                               [+ 新建]                 │
│  ┌───────────────────────────────────────────────────────────────────┐│
│  │ ★ 通用助手 (当前使用)                   [✏️] [🗑]              ││
│  │ 📌 代码助手                              [✏️] [🗑]              ││
│  │ 📌 写作助手                              [✏️] [🗑]              ││
│  └───────────────────────────────────────────────────────────────────┘│
│  智能体详情 (当前选中: 通用助手)                                     │
│  名称: [通用助手            ]  设为默认 ☑                             │
│  描述: [通用对话助手]                                                │
│  系统提示词:                                                         │
│  ┌───────────────────────────────────────────────────────────────────┐│
│  │ 你是一个专业的 AI 助手，请用简洁...                              ││
│  └───────────────────────────────────────────────────────────────────┘│
│  默认模型参数: [跟随供应商默认 ▼]  (或指定 Temperature等)            │
│  MCP 服务器:  ☑ 本地工具服务   ☐ 网络搜索服务                       │
│  技能:        ☑ 代码执行       ☐ 文件管理                           │
│  [💾 保存]  [📤 导出]  [📥 导入]  [↻ 恢复默认]                      │
└───────────────────────────────────────────────────────────────────────┘
```

**偏好设置标签页：**
- 统计显示：显示统计信息、显示费用、显示详细 Token、货币单位、默认展开统计
- 主题：浅色/深色/跟随系统
- 默认智能体：下拉选择
- 快捷键自定义（预留）

### 3.2 左侧面板

**定位：** 窗口最左侧，固定宽度 260px，可折叠至 48px。

| 区域 | 内容 | 交互说明 |
| :--- | :--- | :--- |
| **折叠按钮** | "≡" 图标 | 点击折叠/展开 |
| **对话历史标题** | "📝 对话历史" | 折叠时隐藏 |
| **对话列表** | 所有历史对话列表 | 点击切换；悬停显示删除按钮（内联确认） |
| **新对话按钮** | "✨ 新对话" | 点击创建新会话，自动切到对话视图 |
| **配置入口** | "⚙ 配置"（切换按钮） | 点击切换配置/对话视图，按钮高亮 |

**对话列表项显示：**
```
📝 关于AI的讨论
   💬 12条 · 🔢 2.3K tokens · 🤖 通用助手
```

### 3.3 右侧主区域 - 对话视图（详述）

#### 3.3.1 标题栏
- 当前对话标题（大号）
- 右侧统计：`💬 12条 · 🔢 2.3K tokens · ⏱ 8.7s · 🤖 通用助手`
- 自动命名：新对话默认“新对话”，首条消息后取前20字（清除换行/特殊字符）作为标题。

#### 3.3.2 消息列表
- 用户消息：右对齐，浅色气泡，纯文本。
- AI消息：左对齐，中性气泡，Markdown流式渲染。
- 每条AI消息底部显示统计信息（默认显示 `⏱ 2.3s  │  🔢 201 tokens`，点击 tokens 文字展开详情显示 `📥 45/📤 156/💰 $0.0003`）。
- 自动滚动到底部，用户上滚时暂停自动滚动。

#### 3.3.3 输入区域
| 控件 | 说明 |
| :--- | :--- |
| **智能体选择器** | 下拉框选择当前会话使用的智能体，切换立即生效 |
| **参数折叠按钮** | 展开/折叠参数面板 |
| **连接状态** | ● 已连接 / ○ 未连接（显示在状态栏） |
| **参数面板** | Temperature (0~2)、Max Tokens (1~4096)、Top P (0~1) |
| **多行输入框** | Enter 发送，Shift+Enter 换行 |
| **发送/停止按钮** | 空闲显示“发送”，生成中显示“停止” |

---

## 四、核心交互流程

### 4.1 对话流程（统一智能体驱动）
```
用户发送消息
  ↓
获取当前会话所选智能体配置
  ↓
构建消息历史（包含智能体系统提示词 + 历史消息）
  ↓
准备工具定义（根据智能体关联的 MCP 服务器和技能生成工具列表）
  ↓
调用 IChatClient.GetStreamingResponseAsync()，传入工具定义（若有）
  ↓
模型返回文本或工具调用请求
  ├─ 若为文本 → 流式显示
  └─ 若为工具调用 → 执行工具（调用 MCP 或本地技能）
        ↓
        将工具结果加入上下文
        ↓
        再次调用模型（最多 N 轮，默认 5 轮）
        ↓
        直到模型返回最终文本或达到最大轮数
  ↓
结束，保存消息历史、统计信息
```
- 若智能体未配置任何工具，流程退化为简单的单轮流式文本生成。
- 工具调用循环设置最大轮数，防止无限循环。

### 4.2 中断生成流程
```
用户在 AI 生成过程中点击"停止"按钮
  ↓
触发 CancellationTokenSource.Cancel()
  ↓
取消当前模型调用，并尽量取消正在执行的外部工具（若支持）
  ↓
捕获 OperationCanceledException，AI 消息末尾添加 "[已中断]" 标记
  ↓
统计信息显示已耗时（Token 信息可能不可用）
  ↓
"停止"按钮恢复为"发送"按钮
```

### 4.3 配置流程
```
用户点击 "⚙ 配置"
  ↓
右侧主区域切换到配置视图
  ↓
"⚙ 配置"按钮变为高亮
  ↓
用户修改配置（供应商、智能体、偏好）
  ↓
点击"保存" → 持久化到 SQLite
  ↓
点击"测试连接" → 验证供应商 API 可用性
  ↓
再次点击 "⚙ 配置" 或点击新对话/对话列表
  ↓
回到对话视图
```

### 4.4 对话管理流程（内联删除确认）
1. 用户悬停对话列表项，显示删除按钮（✕）。
2. 点击删除按钮，按钮变为 **“确认删除？”**（红色警告样式）。
3. 用户再次点击该按钮，则删除对话；若点击其他区域或等待 3 秒，则取消确认，恢复原状。

### 4.5 模型列表获取流程
```
用户在 API 配置标签页点击 "获取模型列表"
  ↓
UI 显示加载状态，禁用按钮
  ↓
调用 IModelListService.FetchModelsAsync(config)
  ↓
服务根据供应商预设构建 HTTP 请求，携带 API Key
  ↓
解析响应，返回模型名称列表
  ↓
更新 ProviderConfig.Models（替换或合并）
  ↓
保存配置（或由用户点击保存）
  ↓
UI 刷新模型列表
  ↓
若失败，显示错误提示，保留原列表
```

---

## 五、数据模型设计

### 5.1 内置供应商预设 (ProviderPreset)
定义在应用资源中，不存数据库，用于简化用户配置。

| 字段 | 类型 | 说明 |
| :--- | :--- | :--- |
| Id | string | 唯一标识，如 "openai", "deepseek" |
| DisplayName | string | 显示名称 |
| ProviderType | enum | `OpenAICompatible` 或 `AzureOpenAI`（仅两种） |
| DefaultEndpoint | string | 默认 API 地址 |
| RequiresApiKey | bool | 是否需要 API Key |
| DefaultModels | List<string> | 常见模型列表（初始值） |
| InputPricePer1K | decimal | 默认输入价格 |
| OutputPricePer1K | decimal | 默认输出价格 |
| ModelsEndpoint | string? | 获取模型列表的 API 路径（相对 Endpoint），如 "/models" |
| SupportsModelFetch | bool | 是否支持动态获取模型列表 |

内置预设示例：

| Id | 显示名称 | 类型 | 默认 Endpoint | 需要 Key | 默认模型 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| openai | OpenAI | OpenAICompatible | `https://api.openai.com/v1` | 是 | gpt-4o, gpt-4o-mini |
| deepseek | DeepSeek | OpenAICompatible | `https://api.deepseek.com/v1` | 是 | deepseek-chat |
| ollama | Ollama (本地) | OpenAICompatible | `http://localhost:11434` | 否 | 无（需获取） |
| azure-openai | Azure OpenAI | AzureOpenAI | `https://YOUR_RESOURCE.openai.azure.com/` | 是 | 用户手动部署名 |

### 5.2 供应商配置 (ProviderConfig) - 用户实例

| 字段 | 类型 | 说明 |
| :--- | :--- | :--- |
| Id | string | 配置 ID（唯一） |
| PresetId | string | 关联的内置供应商 ID |
| ApiKey | string | 加密存储的 API Key（可为 null） |
| Endpoint | string? | 覆盖默认 Endpoint（可选） |
| DeploymentName | string? | Azure 专用部署名 |
| Models | List<string> | 最终生效的模型列表（预设+获取+手动） |
| DefaultModel | string | 默认使用的模型 |
| Temperature | double? | 覆盖全局默认参数（可选） |
| MaxTokens | int? | 同上 |
| TopP | double? | 同上 |
| InputPricePer1K | decimal | 可覆盖预设价格 |
| OutputPricePer1K | decimal | 可覆盖预设价格 |
| IsDefault | bool | 是否为默认供应商配置 |
| ModelListUpdatedAt | DateTime? | 模型列表最后成功获取时间 |
| CreatedAt / UpdatedAt | DateTime | 时间戳 |

### 5.3 智能体 (Agent)

| 字段 | 类型 | 说明 |
| :--- | :--- | :--- |
| Id | string | 唯一标识 |
| Name | string | 智能体名称 |
| Description | string? | 描述 |
| SystemPrompt | string | 系统提示词内容 |
| Model | string? | 指定默认模型（可选，覆盖供应商默认） |
| Temperature | double? | 覆盖默认 Temperature（可选） |
| MaxTokens | int? | 覆盖默认 MaxTokens（可选） |
| TopP | double? | 覆盖默认 TopP（可选） |
| McpServerIds | List<string> | 关联的 MCP 服务器 ID 列表 |
| SkillIds | List<string> | 关联的技能 ID 列表 |
| IsDefault | bool | 是否全局默认智能体 |
| IsBuiltIn | bool | 是否内置（不可删除） |
| CreatedAt / UpdatedAt | DateTime | 时间戳 |

### 5.4 MCP 服务器配置 (McpServerConfig)

| 字段 | 类型 | 说明 |
| :--- | :--- | :--- |
| Id | string | 唯一标识 |
| Name | string | 服务器名称 |
| Transport | enum | 传输类型：Stdio / SSE / StreamableHTTP |
| Command | string? | Stdio 模式下的命令 |
| Args | List<string>? | Stdio 模式下的参数 |
| Url | string? | SSE/HTTP 模式下的 URL |
| Headers | Dictionary<string,string>? | HTTP 头部 |
| Enabled | bool | 是否启用 |
| CreatedAt / UpdatedAt | DateTime | 时间戳 |

### 5.5 技能定义 (SkillDefinition)

| 字段 | 类型 | 说明 |
| :--- | :--- | :--- |
| Id | string | 唯一标识 |
| Name | string | 技能名称 |
| Description | string | 技能描述（供模型理解） |
| ToolSpec | string | 工具 JSON Schema 或 OpenAPI 定义 |
| Enabled | bool | 是否启用 |
| CreatedAt / UpdatedAt | DateTime | 时间戳 |

### 5.6 对话会话 (ChatSession)

| 字段 | 类型 | 说明 |
| :--- | :--- | :--- |
| Id | string | 唯一标识 |
| Title | string | 对话标题 |
| Messages | List<ChatMessage> | 消息列表 |
| ProviderId | string | 使用的供应商配置 ID |
| AgentId | string | 使用的智能体 ID |
| AgentSnapshot | string | 智能体配置 JSON 快照（用于历史回放） |
| ModelUsed | string | 实际使用的模型名称 |
| CreatedAt / UpdatedAt | DateTime | 时间戳 |

### 5.7 消息 (ChatMessage)

| 字段 | 类型 | 说明 |
| :--- | :--- | :--- |
| Id | string | 唯一标识 |
| Role | string | User / Assistant / System / Tool |
| Content | string | 消息内容 |
| Timestamp | DateTime | 时间戳 |
| StartTime | DateTime? | 请求开始时间 |
| EndTime | DateTime? | 请求结束时间 |
| DurationMs | long? | 耗时 |
| PromptTokens | int? | 输入 Token |
| CompletionTokens | int? | 输出 Token |
| TotalTokens | int? | 合计 Token |
| EstimatedCost | decimal? | 预估费用 |
| ModelUsed | string? | 实际使用的模型 |
| IsInterrupted | bool | 是否被中断 |

### 5.8 用户偏好 (UserPreferences)

| 字段 | 类型 | 说明 |
| :--- | :--- | :--- |
| ShowStatistics | bool | 默认 true |
| ShowCost | bool | 默认 true |
| ShowDetailedTokens | bool | 默认 false |
| Currency | string | "USD" 或 "CNY" |
| ExpandStatisticsByDefault | bool | 默认 false |
| LeftPanelCollapsed | bool | 默认 false |
| SelectedProviderId | string | 当前选中的供应商配置 ID |
| DefaultAgentId | string | 默认智能体 ID |
| Theme | string | "Light", "Dark", "System" |
| Language | string | "zh-CN" |

---

## 六、数据存储设计

### 6.1 存储方案
采用 **SQLite** 作为主存储，数据库文件 `llmchat.db` 放置在用户应用数据目录下。

### 6.2 存储位置

| 平台 | 路径 |
| :--- | :--- |
| Windows | `%AppData%\LLMChatApp\` |
| macOS | `~/Library/Application Support/LLMChatApp/` |
| Linux | `~/.config/LLMChatApp/` |

### 6.3 SQLite 表结构

```sql
CREATE TABLE Providers (
    Id TEXT PRIMARY KEY,
    PresetId TEXT NOT NULL,
    ApiKey TEXT,                      -- 加密存储
    Endpoint TEXT,                    -- 可为 NULL
    DeploymentName TEXT,              -- 可为 NULL
    Models TEXT NOT NULL,             -- JSON 数组
    DefaultModel TEXT NOT NULL,
    Temperature REAL,                 -- 可为 NULL
    MaxTokens INTEGER,                -- 可为 NULL
    TopP REAL,                        -- 可为 NULL
    InputPricePer1K REAL NOT NULL,
    OutputPricePer1K REAL NOT NULL,
    IsDefault INTEGER NOT NULL,
    ModelListUpdatedAt TEXT,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);

CREATE TABLE Agents (
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    Description TEXT,
    SystemPrompt TEXT NOT NULL,
    Model TEXT,
    Temperature REAL,
    MaxTokens INTEGER,
    TopP REAL,
    McpServerIds TEXT,                -- JSON 数组
    SkillIds TEXT,                    -- JSON 数组
    IsDefault INTEGER NOT NULL,
    IsBuiltIn INTEGER NOT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);

CREATE TABLE McpServers (
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    Transport TEXT NOT NULL,
    Command TEXT,
    Args TEXT,                        -- JSON 数组
    Url TEXT,
    Headers TEXT,                     -- JSON 对象
    Enabled INTEGER NOT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);

CREATE TABLE Skills (
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    Description TEXT NOT NULL,
    ToolSpec TEXT NOT NULL,           -- JSON Schema
    Enabled INTEGER NOT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);

CREATE TABLE Sessions (
    Id TEXT PRIMARY KEY,
    Title TEXT NOT NULL,
    ProviderId TEXT NOT NULL,
    AgentId TEXT NOT NULL,
    AgentSnapshot TEXT,               -- JSON 快照
    ModelUsed TEXT,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    FOREIGN KEY (ProviderId) REFERENCES Providers(Id)
);

CREATE TABLE Messages (
    Id TEXT PRIMARY KEY,
    SessionId TEXT NOT NULL,
    Role TEXT NOT NULL,
    Content TEXT NOT NULL,
    Timestamp TEXT NOT NULL,
    StartTime TEXT,
    EndTime TEXT,
    DurationMs INTEGER,
    PromptTokens INTEGER,
    CompletionTokens INTEGER,
    TotalTokens INTEGER,
    EstimatedCost REAL,
    ModelUsed TEXT,
    IsInterrupted INTEGER NOT NULL,
    FOREIGN KEY (SessionId) REFERENCES Sessions(Id) ON DELETE CASCADE
);

CREATE INDEX idx_sessions_updated ON Sessions(UpdatedAt);
CREATE INDEX idx_messages_session ON Messages(SessionId);
```

### 6.4 API Key 加密
使用 `Microsoft.AspNetCore.DataProtection` 的 `IDataProtectionProvider` 进行跨平台加密，密钥存储在用户本地（Windows: DPAPI, macOS: Keychain, Linux: libsecret 或文件保护）。

### 6.5 数据库备份
应用退出前自动执行 `VACUUM INTO` 将数据库备份到 `llmchat.backup.db`，每次启动时检查备份完整性。

---

## 七、API 集成设计

### 7.1 内置供应商预设
应用内置 `ProviderPreset` 列表，包含 OpenAI、DeepSeek、Ollama、Azure OpenAI 等。用户选择预设后，只需填写 API Key（如果需要）和选择模型即可使用。预设还定义了模型获取端点等信息。

### 7.2 IChatClientFactory
根据 `ProviderConfig` 的 `PresetId` 和 `ProviderType` 创建客户端：

| ProviderType | 实现 |
| :--- | :--- |
| OpenAICompatible | `new OpenAIClient(new Uri(endpoint), apiKey).AsChatClient(model)`，可附加自定义 Headers |
| AzureOpenAI | `new AzureOpenAIClient(new Uri(endpoint), credential).AsChatClient(deploymentName)` |

### 7.3 模型列表获取服务 (IModelListService)
```csharp
public interface IModelListService
{
    Task<List<string>> FetchModelsAsync(ProviderConfig config, CancellationToken ct = default);
}
```
实现逻辑：
- 从对应 `ProviderPreset` 获取 `ModelsEndpoint` 和 HTTP 方法。
- 构造请求，携带 API Key（如 `Authorization: Bearer` 或 `x-api-key`）。
- 解析响应，支持不同格式（OpenAI 返回 `data[].id`，Ollama 返回 `models[].name`）。
- 返回模型名称列表。

### 7.4 流式调用与工具支持
对话编排器 `IAgentOrchestrator` 负责：
- 构建消息历史（包括系统提示词、历史消息）。
- 获取智能体关联的启用工具列表（来自 MCP 服务器和技能）。
- 将工具以 `ChatOptions.Tools` 或 `AdditionalProperties` 方式传入。
- 处理流式输出和工具调用循环。

代码示例：
```csharp
public async IAsyncEnumerable<string> RunAsync(
    ChatSession session,
    Agent agent,
    ProviderConfig provider,
    CancellationToken ct)
{
    var history = BuildHistory(agent, session.Messages);
    var tools = await GetToolsAsync(agent, ct);
    var options = new ChatOptions
    {
        Tools = tools,
        AdditionalProperties = { ["stream_options"] = new { include_usage = true } }
    };

    while (true)
    {
        var response = _chatClient.GetStreamingResponseAsync(history, options, ct);
        await foreach (var update in response)
        {
            if (update.Text != null)
                yield return update.Text;
            if (update.ToolCalls != null)
            {
                foreach (var toolCall in update.ToolCalls)
                {
                    var result = await ExecuteToolAsync(toolCall, ct);
                    history.Add(new ChatMessage(ChatRole.Tool, result));
                }
                // 继续循环调用模型
                break;
            }
            if (update.Usage != null)
                _lastUsage = update.Usage;
        }
        // 若没有工具调用则结束
        if (!hasToolCalls) break;
    }
}
```

### 7.5 Token Usage 获取策略
- 优先从流式更新的 `Usage` 属性获取（需 API 支持 `stream_options: { include_usage: true }`）。
- 若未获取，尝试从底层 SDK 的最后响应中获取。
- 若仍无，根据内容长度估算。
- 若均失败，显示 `--`。

---

## 八、智能体管理与工具调用

### 8.1 智能体管理
智能体是对话的核心，每个智能体可关联多个 MCP 服务器和技能，用于扩展模型能力。

### 8.2 MCP 集成
`IMcpClientService` 负责与 MCP 服务器通信：
- 支持 Stdio、SSE、StreamableHTTP 传输。
- 启动时或按需建立连接，发现工具列表。
- 执行工具调用并返回结果。
- 连接失败时自动禁用该 MCP 服务器，并在状态栏提示。

### 8.3 技能集成
`ISkillService` 管理自定义技能：
- 技能通过 `ToolSpec` 定义工具 JSON Schema。
- 执行时调用本地实现（如运行代码、文件操作等）。
- 技能可作为工具提供给模型。

### 8.4 工具调用循环
- 最大轮数默认 5 次，防止无限循环。
- 每轮工具调用后，将工具结果（包含错误信息）反馈给模型。
- 若工具执行超时或异常，将错误信息作为结果返回。

### 8.5 内置智能体
- **通用助手**：无工具，系统提示词为通用助手，作为默认智能体。
- **代码助手**：示例智能体，可关联一个代码执行技能，用于演示。

---

## 九、Token 统计与费用计算

### 9.1 费用计算公式
```
预估费用 = (输入Tokens / 1000) × 输入单价 + (输出Tokens / 1000) × 输出单价
```

### 9.2 默认模型定价预设
在“模型定价”区域提供下拉选择常用模型，自动填充价格（数据来自内置预设）。

### 9.3 统计信息显示交互
- 默认显示：`⏱ 2.3s  │  🔢 201 tokens`
- 点击 `🔢 201 tokens` 文字，展开详情：`⏱ 2.3s  │  📥 45  │  📤 156  │  💰 $0.0003`
- 再次点击折叠。

### 9.4 费用显示格式
| 费用范围 | 显示格式 |
| :--- | :--- |
| < $0.0001 | `< $0.0001` |
| ≥ $0.0001 且 < $1 | `$0.XXXXX` |
| ≥ $1 | `$X.XX` |
| 无法计算 | `--` |

---

## 十、异常处理与用户体验

### 10.1 异常映射

| HTTP 状态码 / 异常类型 | 用户友好信息 |
| :--- | :--- |
| 401 | ❌ 认证失败，请检查 API Key |
| 403 | ❌ 权限不足 |
| 404 | ❌ 端点或模型不存在 |
| 429 | ⚠️ API 配额已用尽，请稍后重试 |
| 408 / TimeoutException | ⏰ 请求超时，请检查网络 |
| HttpRequestException (其他) | ⚠️ 网络连接异常 |
| OperationCanceledException | （用户取消，静默处理） |
| JsonException | ⚠️ 数据解析错误，请检查 API 响应 |
| MCP 连接失败 | ⚠️ MCP 服务器连接失败，工具不可用 |
| 工具执行超时 | ⚠️ 工具执行超时 |
| 其他 | ⚠️ 未知错误，请查看日志 |

### 10.2 全局状态栏
位于主窗口底部，显示：
- 连接状态（● 已连接 / ○ 未连接）
- 当前供应商和模型（如“OpenAI · gpt-4o-mini”）
- 当前智能体（如“🤖 通用助手”）
- 操作状态（“保存中...”、“生成中...”等）
- 错误/成功信息（短暂显示后自动消失）

### 10.3 删除确认（内联方式）
已明确，请参见第 4.4 节。

---

## 十一、性能优化与 AOT 兼容

### 11.1 性能优化策略
- 消息列表虚拟化（超过 100 条消息时启用）
- Markdown 增量渲染（仅更新变化部分）
- 启动时懒加载：只加载最近 20 条对话摘要，点击对话时再加载完整消息
- 数据库 WAL 模式提升并发读取性能
- 异步 I/O 全部使用 async/await

### 11.2 性能目标
| 指标 | 目标值 |
| :--- | :--- |
| 冷启动至窗口可见 | < 500ms（加载摘要异步进行） |
| 空闲内存 | < 80MB |
| 对话中内存 | < 150MB |
| 安装包大小 | < 50MB（AOT）或 < 80MB（SingleFile） |

### 11.3 AOT 兼容性策略
- **验证阶段（Sprint 0）：** 创建最小原型，集成 `Avalonia` + `Microsoft.Extensions.AI.OpenAI` + `LiveMarkdown.Avalonia`，尝试 Native AOT 编译。
- **决策点：**
  - **成功** → 继续使用 AOT。
  - **失败** → 回退到 `PublishSingleFile`（不启用 AOT），仍为单文件发布，但依赖运行时，兼容性最佳。
- **文档标注：** 在项目 README 中说明推荐发布模式。

---

## 十二、项目文件结构

```
LLMChatApp/
├── LLMChatApp/
│   ├── Assets/
│   │   ├── Icons/
│   │   └── Fonts/
│   ├── Controls/                     # 所有自定义控件
│   │   ├── MarkdownScrollViewer.axaml
│   │   ├── MessageBubble.axaml
│   │   └── StatisticsBadge.axaml
│   ├── Models/
│   │   ├── ProviderConfig.cs
│   │   ├── ProviderPreset.cs
│   │   ├── Agent.cs
│   │   ├── McpServerConfig.cs
│   │   ├── SkillDefinition.cs
│   │   ├── ChatSession.cs
│   │   ├── ChatMessage.cs
│   │   └── UserPreferences.cs
│   ├── Services/
│   │   ├── IConfigService.cs / ConfigService.cs
│   │   ├── ISessionService.cs / SessionService.cs
│   │   ├── ITokenCostCalculator.cs / TokenCostCalculator.cs
│   │   ├── IChatClientFactory.cs / ChatClientFactory.cs
│   │   ├── IModelListService.cs / ModelListService.cs
│   │   ├── IAgentOrchestrator.cs / AgentOrchestrator.cs
│   │   ├── IMcpClientService.cs / McpClientService.cs
│   │   ├── ISkillService.cs / SkillService.cs
│   │   └── AOTCompatibility.cs
│   ├── ViewModels/
│   │   ├── ViewModelBase.cs
│   │   ├── MainWindowViewModel.cs
│   │   ├── ChatSessionViewModel.cs
│   │   ├── MessageViewModel.cs
│   │   └── ProviderConfigViewModel.cs
│   ├── Views/
│   │   ├── MainWindow.axaml
│   │   ├── MainWindow.axaml.cs
│   │   └── Converters/
│   │       ├── BoolToVisibilityConverter.cs
│   │       ├── BoolToClassConverter.cs
│   │       └── UserMessageColorConverter.cs
│   ├── Helpers/
│   │   ├── EncryptionHelper.cs
│   │   ├── FileSystemHelper.cs
│   │   └── ModelPricingHelper.cs
│   ├── Resources/
│   │   ├── Strings.resx
│   │   └── Strings.zh-CN.resx
│   ├── App.axaml
│   ├── App.axaml.cs
│   └── Program.cs
├── LLMChatApp.Tests/
│   ├── Services/
│   └── ViewModels/
├── LLMChatApp.sln
└── README.md
```

---

## 十三、构建与发布

### 13.1 开发环境要求
| 组件 | 版本 |
| :--- | :--- |
| .NET SDK | 10.0 |
| Avalonia UI | 11.x |
| IDE | Visual Studio 2022 / Rider / VS Code |
| OS | Windows 10+ / macOS 12+ / Linux (Ubuntu 20.04+) |

### 13.2 项目配置（csproj）
```xml
<PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <AvaloniaUseCompiledBindings>true</AvaloniaUseCompiledBindings>
    <!-- AOT 配置，可根据验证结果启用 -->
    <PublishAot Condition="'$(AotEnabled)' == 'true'">true</PublishAot>
    <JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
</PropertyGroup>
```

### 13.3 发布命令

**AOT 发布（若验证成功）：**
```bash
dotnet publish -c Release -r win-x64 -p:AotEnabled=true --self-contained
```

**SingleFile 发布（备选）：**
```bash
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true --self-contained
```

**各平台 RID：** `win-x64`, `osx-x64`, `osx-arm64`, `linux-x64`

### 13.4 打包分发
- **Windows**: MSIX 或 Inno Setup 安装包（含签名）
- **macOS**: 构建 `.app`，进行代码签名和公证（Notarization）
- **Linux**: AppImage 或 `.deb` 包

---

## 十四、路线图与扩展方向

### 14.1 v1.0（当前）
所有已设计功能。

### 14.2 v1.1
- 对话导出（Markdown/PDF）
- 多语言支持（资源文件已就绪）
- 对话搜索
- 语音输入（可选）

### 14.3 v2.0
- 多模型对比
- 插件系统
- 云端同步
- 智能体市场（分享与下载）

---

## 十五、附录

### A. 快捷键汇总
| 快捷键 | 功能 |
| :--- | :--- |
| `Enter` | 发送消息 |
| `Shift+Enter` | 换行 |
| `Ctrl+N` | 新建对话 |
| `Ctrl+,` | 切换配置面板 |
| `Esc` | 关闭配置面板 |
| `Ctrl+Shift+C` | 复制当前 AI 回复 |
| `Ctrl+1-9` | 切换到对应编号的对话 |

### B. 术语表
| 术语 | 说明 |
| :--- | :--- |
| **LLM** | 大语言模型 |
| **AOT** | Ahead-Of-Time 编译 |
| **MVVM** | Model-View-ViewModel 架构模式 |
| **Token** | 文本的最小处理单元 |
| **智能体 (Agent)** | 包含系统提示词、工具配置的对话实例 |
| **MCP** | Model Context Protocol，用于工具集成 |
| **技能 (Skill)** | 自定义本地工具 |
| **流式输出** | 逐步生成并显示内容 |
| **Native AOT** | 原生提前编译 |
