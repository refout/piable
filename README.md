# Piable

基于 Avalonia UI 的跨平台桌面 LLM 对话应用。

## 当前状态

首版聚焦"能真正用来聊天"的核心闭环。**MCP 与技能系统尚未实现**——数据结构、数据库表与界面入口都已就位，但既不建立 MCP 连接，也不执行技能，因此对话不会携带任何外部工具（见下方[路线图](#路线图)）。

### 已实现

| 模块 | 说明 |
| :--- | :--- |
| **供应商配置** | 4 个内置预设（OpenAI / DeepSeek / Ollama / Azure OpenAI），API Key 加密落库，模型列表动态获取与手动增删，连接测试 |
| **智能体管理** | 内置「通用助手」与「代码助手」，支持增删改、系统提示词、模型与采样参数覆盖、设为默认 |
| **流式对话** | 逐字流式输出，Enter 发送 / Shift+Enter 换行，生成中可停止并标记「已中断」 |
| **Markdown 渲染** | 基于 LiveMarkdown 的增量渲染，支持标题、列表、表格、引用、带语法高亮的代码块 |
| **统计与费用** | 每次回答的耗时、输入/输出 Token 与费用估算，点击可在摘要与明细间切换 |
| **会话管理** | 新建、切换、按首条消息自动命名、内联二次确认删除 |
| **偏好设置** | 统计显示开关、浅色/深色/跟随系统主题、货币单位、默认智能体、请求超时 |
| **本地持久化** | SQLite（WAL 模式），退出时自动备份，启动时主库损坏可自动从备份恢复 |

### 未实现

- **MCP 客户端**：不支持 Stdio / SSE / StreamableHTTP 连接与工具发现
- **技能系统**：不支持自定义工具的定义与执行
- **工具调用循环**：编排器已预留结构，但当前恒为单轮生成
- **智能体导出 / 导入**
- **多语言**：界面文案为中文硬编码，尚未走资源文件
- **对话导出界面**：`SessionService.ExportAsMarkdown` 已实现但未接入界面

## 技术栈

| 类别 | 方案 | 版本 |
| :--- | :--- | :--- |
| 运行时 | .NET | 10.0 |
| UI 框架 | Avalonia UI | 12.1.2 |
| MVVM | CommunityToolkit.Mvvm | 8.4.2 |
| AI 抽象 | Microsoft.Extensions.AI + .OpenAI | 10.10.0 |
| Azure 适配 | Azure.AI.OpenAI | 2.1.0 |
| Markdown | LiveMarkdown.Avalonia | 2.4.0 |
| 数据存储 | Microsoft.Data.Sqlite | 10.0.12 |
| 测试 | xunit v3 + Avalonia.Headless | 3.2.2 / 12.1.2 |

> 设计文档写的是 Avalonia 11.x，但 LiveMarkdown.Avalonia 2.4.0 硬依赖 Avalonia ≥ 12.0.0，
> 两者无法共存，因此采用 Avalonia 12。

## 开发

```bash
dotnet build                              # 构建
dotnet test                               # 运行全部测试
dotnet run --project src/Piable           # 启动应用
```

需要 .NET 10 SDK。Avalonia 模板可通过 `dotnet new install Avalonia.Templates` 安装（非必需）。

## 项目结构

```
src/Piable/
├── Models/           领域模型与 JSON 源生成器上下文
├── Services/         业务服务
│   └── Storage/      SQLite 连接、建表与各仓储
├── ViewModels/       MVVM 的 ViewModel 层
├── Views/            XAML 视图
├── Helpers/          路径解析与 AES-GCM 密钥保护
└── App.axaml(.cs)    依赖注入与应用启动
tests/Piable.Tests/
├── Models/  Services/  Storage/  ViewModels/   单元与集成测试
└── Ui/                                          无头布局测试
tools/                                           本地截图辅助脚本
```

## 数据存储

| 平台 | 路径 |
| :--- | :--- |
| Windows | `%AppData%\Piable\` |
| macOS | `~/Library/Application Support/Piable/` |
| Linux | `~/.config/Piable/` |

目录内包含 `piable.db`（主库）、`piable.key`（加密密钥）、`piable.backup.db`（退出时备份）。

## 安全说明

**API Key 的加密边界。** 密钥以 AES-256-GCM 加密后存入数据库，密钥文件为应用数据目录下的 `piable.key`（Unix 上权限设为 `0600`；Windows 依赖 `%AppData%` 继承的仅当前用户 ACL，程序本身不额外收紧权限）。

这防的是"数据库文件被单独拷走"这类场景——**不能**防御能以当前用户身份读取文件系统的攻击者，因为密钥与密文同处一个目录。这相当于 DataProtection 使用文件密钥仓时的保护级别，不是硬件密钥库。

`piable.key` 一旦删除，此前保存的 API Key 将无法解密，需要重新填写。

## 已知限制

**`max_completion_tokens` 的兼容性。** OpenAI SDK 2.x 发送的是 `max_completion_tokens` 而非旧的 `max_tokens`。对只认 `max_tokens` 的兼容端点，该参数可能被忽略（表现为 Max Tokens 设置无效），少数严格的实现可能直接报错。这是 SDK 的行为，本项目未做改写。

**定价按供应商而非按模型。** 一个供应商配置只有一组输入/输出单价，若同一供应商下同时使用 `gpt-4o` 与 `gpt-4o-mini`，费用估算会偏向其中一个。内置的定价下拉表仅作填充便利，**价格可能随官方调整而过期**，可自行覆盖。

**Token 估算。** 供应商未返回 usage 时（例如被中断的回答不估算），会按字符数粗估：CJK 字符按 1 token、其余按 4 字符 1 token。误差可能较大。

**请求超时是总时长上限**，不是空闲检测。默认 300 秒，长回答可在偏好设置里调大。

## 测试

```bash
dotnet test
```

当前 216 项测试，覆盖序列化、加密、存储 CRUD、费用计算、错误映射、流式链路与界面布局。

其中两类值得单独说明：

- **本地 mock 服务端**（`MockOpenAiServer`）：测试通过真实的 OpenAI SDK 打到本机 HTTP 服务端，覆盖请求构造、SSE 解析、usage 采集与错误状态码。只 mock `IChatClient` 会绕开最易出错的一层。
- **无头布局测试**（`MainWindowLayoutTests`）：在 Avalonia Headless 下真实跑布局，断言输入区、状态栏等关键控件确实渲染在窗口范围内。编译期绑定只能保证属性名没写错，保证不了元素没被挤出可视区域。

## 发布

```bash
# 单文件发布（当前推荐）
dotnet publish src/Piable -c Release -r win-x64 -p:PublishSingleFile=true --self-contained

# Native AOT（体积更小，但需先装 MSVC C++ 工具链）
dotnet publish src/Piable -c Release -r win-x64 -p:AotEnabled=true --self-contained
```

各平台 RID：`win-x64`、`osx-x64`、`osx-arm64`、`linux-x64`。

### 实测结果

**单文件发布**（已在本机验证可正常运行）：产出单个 `Piable.exe`，**53 MB**。
原生库经
`IncludeNativeLibrariesForSelfExtract` 打包并通过 `EnableCompressionInSingleFile` 压缩；
SkiaSharp 与 HarfBuzz 附带的 `.pdb`（合计约 100 MB）已通过 `ExcludeNativeSymbolsFromPublish`
目标从发布列表中剔除。

**Native AOT**（未完成验证）：IL 编译阶段**零 AOT/裁剪警告**通过——这覆盖了 Avalonia、
OpenAI SDK、Microsoft.Extensions.AI、LiveMarkdown 与 Microsoft.Data.Sqlite 的全部可达代码，
说明没有触发 `IL2xxx`/`IL3xxx` 那类反射与动态代码告警。失败点在最后的本机链接步骤：

```
error : Platform linker not found. Ensure you have all the required prerequisites...
```

本机未安装 Visual Studio 的「使用 C++ 的桌面开发」工作负载，缺少 `link.exe`。
**因此 AOT 的最终可用性尚未证实**，需要在装有 MSVC 的机器上重新验证后才能下结论。
在那之前推荐使用单文件发布。

### AOT 友好性措施

项目按 AOT 友好的方式编写，这也是 IL 编译阶段零告警的主要原因：

- JSON 全部走源生成器，并设置了 `JsonSerializerIsReflectionEnabledByDefault=false`——
  任何误用反射的序列化都会在运行期直接抛异常，而不是静默降级到反射路径
- 依赖注入全部使用显式工厂委托，未使用 `AddSingleton<TInterface, TImpl>()` 的反射激活
- 未使用 Avalonia 模板生成的 `ViewLocator`（它依赖 `Type.GetType` 与 `Activator.CreateInstance`），
  单窗口应用的视图切换用 `IsVisible` 表达即可

## 路线图

1. **MCP 客户端**：Stdio / SSE / StreamableHTTP 传输、工具发现与执行
2. **技能系统**：自定义工具定义与本地执行
3. **工具调用循环**：多轮工具调用与结果回填
4. 对话导出（Markdown / PDF）与对话搜索
5. 智能体导出与导入
6. 多语言资源文件
