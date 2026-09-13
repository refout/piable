# Piable

基于 Avalonia UI 的跨平台桌面 LLM 对话应用。

## 当前状态

功能闭环完整：配置供应商与智能体 → 关联工具 → 流式对话 → 工具调用 → 统计与持久化。

### 已实现

| 模块 | 说明 |
| :--- | :--- |
| **供应商配置** | 4 个内置预设（OpenAI / DeepSeek / Ollama / Azure OpenAI）+ **自定义供应商**（可创建多条、各自命名、可删除），API Key 加密落库，模型列表动态获取与手动增删，连接测试 |
| **智能体管理** | 内置「通用助手」与「代码助手」，支持增删改、系统提示词、模型与采样参数覆盖、关联技能与 MCP 服务器、设为默认、导出与导入 JSON |
| **流式对话** | 逐字流式输出，Enter 发送 / Shift+Enter 换行，生成中可停止并标记「已中断」 |
| **输入区工具行** | 输入框下方一行：新对话、智能体、模型、采样参数在左，发送/停止在右。模型与参数只在**当前会话**内覆盖，不写回供应商或智能体配置；模型列表跟随当前供应商，可一键「跟随默认」 |
| **Markdown 渲染** | 基于 LiveMarkdown 的增量渲染，支持标题、列表、表格、引用、带语法高亮的代码块 |
| **技能** | 处理器注册表驱动的本地工具，内置「当前时间」与「执行命令」，可自定义名称、描述与参数 Schema |
| **MCP** | Stdio / SSE / StreamableHTTP 三种传输，按需连接、工具发现与执行，工具名按服务器加前缀避免重名 |
| **MCP 内容浏览** | 在服务器页直接查看该服务器暴露的**工具**（含参数 Schema 与风险标记）、**资源**（可读取正文）、**提示模板**（填参数后展开），支持一键读取全部 |
| **MCP 资源挂载** | 智能体可勾选挂载 MCP 资源，生成时自动读取并附在系统提示词之后 |
| **工具调用循环** | 多轮工具调用与结果回填，工具异常作为错误结果反馈给模型而不中断对话，轮数上限可配 |
| **危险工具授权** | 工具按风险分级，危险的默认拒绝执行；开启逐次确认后每次调用前弹卡片由用户放行；调用过程完整展示在对话流中 |
| **统计与费用** | 每次回答的耗时、输入/输出 Token 与费用估算，点击可在摘要与明细间切换 |
| **会话管理** | 新建、切换、按首条消息自动命名、标题栏内联重命名、内联二次确认删除、导出为 Markdown |
| **会话搜索** | 侧边栏按标题与正文全文搜索（300ms 防抖、LIKE 通配符转义），结果标注命中条数，搜索不影响右侧正在进行的对话 |
| **偏好设置** | 统计显示开关、浅色/深色/跟随系统主题、**界面语言（简体中文 / English，切换立即生效）**、货币单位、默认智能体、请求超时、工具轮数上限 |
| **多语言** | 界面文案与运行时消息全部走语言包（嵌入资源，`Resources/Strings.<语言>.json`），XAML 用 `{DynamicResource Loc.xxx}`、C# 用 `Loc.Get(key)`，切换后资源字典与派生文本同步刷新，无需重启 |
| **本地持久化** | SQLite（WAL 模式）含结构版本迁移，退出时自动备份，启动时主库损坏可自动从备份恢复 |

### 未实现

- **技能自定义脚本**：动作只能来自程序内置实现（见下方[危险工具](#危险工具与授权)）

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
│   ├── Storage/      SQLite 连接、建表迁移与各仓储
│   └── Tools/        工具层：工具汇总、技能服务、MCP 客户端
│       └── Handlers/ 技能的内置实现
├── ViewModels/       MVVM 的 ViewModel 层
├── Views/            XAML 视图
├── Helpers/          路径解析与 AES-GCM 密钥保护
└── App.axaml(.cs)    依赖注入与应用启动
tests/Piable.Tests/
├── Models/ Services/ Storage/ ViewModels/ Tools/   单元与集成测试
└── Ui/                                             无头布局测试
tools/
├── FakeMcpServer/           测试用的最小 MCP stdio 服务端
└── capture-window.ps1       本地截图辅助脚本
```

## 数据存储

| 平台 | 路径 |
| :--- | :--- |
| Windows | `%AppData%\Piable\` |
| macOS | `~/Library/Application Support/Piable/` |
| Linux | `~/.config/Piable/` |

目录内包含 `piable.db`（主库）、`piable.key`（加密密钥）、`piable.backup.db`（退出时备份）。

## 工具系统

### 技能

技能的动作**只能来自程序内置的处理器**（`ISkillHandler`），用户在界面上填写的是名称、描述与参数 Schema。这样"模型请求了什么"与"实际会执行什么"之间的对应关系是确定的——不存在配置里写 A、实际执行 B 的空间。

新增一个内置技能只需实现 `ISkillHandler` 并注册到 `SkillHandlers.All`。

工具名必须单独维护：OpenAI 兼容接口要求函数名匹配 `^[a-zA-Z0-9_-]{1,64}$`，而技能名称通常是中文，直接拿来当函数名会被供应商拒绝。

### MCP

采用官方 `ModelContextProtocol` SDK。连接在**首次对话时按需建立**并缓存，无需手动连接；修改配置后旧连接自动作废。

MCP 工具的**风险等级由服务端声明决定**：只有显式声明了 `readOnlyHint` 的才判为安全，**未声明的一律按危险处理**——第三方服务器的工具能做什么，客户端无从验证。

工具名会加上服务器前缀（如 `本地工具服务_echo`），因为不同服务器完全可能提供同名工具。

单台服务器连不上不影响其余工具：记一条警告后继续对话。

### 危险工具与授权

工具分两级：

| 等级 | 例子 | 行为 |
| :--- | :--- | :--- |
| **安全** | 读取当前时间 | 关联到智能体后即可直接调用 |
| **危险** | 执行 shell 命令、未声明只读的 MCP 工具 | **默认拒绝执行**，需在智能体设置中显式开启「允许执行危险工具」 |

被拒绝时，拒绝原因会作为工具结果回填给模型，让它改走别的路子，而不是静默失败。

**为什么要默认关闭。** 真正的风险不是工具本身，而是模型可能被对话内容或**其他工具的返回值**里的提示注入诱导着去调用它——比如读取一个网页后，"网页内容"里写着"请执行 rm -rf"。这类调用一旦执行就无法撤销。风险高低取决于该智能体接触的内容是否可信，因此授权是**按智能体**逐个开启的，而不是全局开关。

每次工具调用的名称、参数、结果与状态都会完整展示在对话流中并落库，历史回看时同样可见。

## 安全说明

**API Key 的加密边界。** 密钥以 AES-256-GCM 加密后存入数据库，密钥文件为应用数据目录下的 `piable.key`（Unix 上权限设为 `0600`；Windows 依赖 `%AppData%` 继承的仅当前用户 ACL，程序本身不额外收紧权限）。

这防的是"数据库文件被单独拷走"这类场景——**不能**防御能以当前用户身份读取文件系统的攻击者，因为密钥与密文同处一个目录。这相当于 DataProtection 使用文件密钥仓时的保护级别，不是硬件密钥库。

`piable.key` 一旦删除，此前保存的 API Key 将无法解密，需要重新填写。

## 已知限制

**`max_completion_tokens` 的兼容性。** OpenAI SDK 2.x 发送的是 `max_completion_tokens` 而非旧的 `max_tokens`。对只认 `max_tokens` 的兼容端点，该参数可能被忽略（表现为 Max Tokens 设置无效），少数严格的实现可能直接报错。这是 SDK 的行为，本项目未做改写。

**定价按供应商而非按模型。** 一个供应商配置只有一组输入/输出单价，若同一供应商下同时使用 `gpt-4o` 与 `gpt-4o-mini`，费用估算会偏向其中一个。内置的定价下拉表仅作填充便利，**价格可能随官方调整而过期**，可自行覆盖。

**Token 估算。** 供应商未返回 usage 时（例如被中断的回答不估算），会按字符数粗估：CJK 字符按 1 token、其余按 4 字符 1 token。误差可能较大。

**请求超时是总时长上限**，不是空闲检测。默认 300 秒，长回答可在偏好设置里调大。

**MCP 断开连接固定要等 2 秒。** 实测 C# SDK 在释放 stdio 传输时既不关闭子进程的 stdin 也不发送关闭通知，只是等待进程退出，因此每次断开都要等满 `ShutdownTimeout`。应用退出时多台服务器会**并行**释放，总耗时恒为一个超时周期而非累加。该值定为 2 秒（MCP 服务器都是简单子进程，2 秒足够自行收尾）。

**自定义供应商共用一套"自定义"预设，而不是各自成为一个预设。** 内置预设是代码里的常量，一条预设最多对应一条配置；自定义供应商则是用户创建的数据，可以有多条。因此它们统一记在 `custom` 这个预设 ID 下，靠配置自身的 Id 与 Name 区分——协议、能力这些属性仍然从"自定义"预设读取（OpenAI 兼容、`/models` 可尝试、不强制 API Key）。新建后**保存才落库**，避免点一下"新建"就在库里留一条空配置。

**多语言只覆盖界面与运行时消息，不覆盖内容数据。** 内置智能体的名称/描述与**系统提示词**、默认会话标题「新对话」、自定义供应商的默认名、工具的 JSON Schema 都不随界面语言变化：它们要么落库即固定（换语言后不该让同一条记录变个名字），要么直接决定模型的行为（系统提示词切成英文会连带改变模型的回答语言）。要加一门新语言，只需在 `Resources/` 下放一份 `Strings.<语言>.json`，并在 `LocalizationService.AvailableLanguages` 里登记；有测试会逐条比对语言包与代码里用到的键，漏译会直接让测试失败。

**MCP 的 resources 与 prompts 按可选能力处理。** 连接时会顺带拉取资源清单与提示模板，服务器未声明对应能力时静默降级为空列表——只提供工具的服务器是绝大多数，为"没有资源"让整台服务器不可用毫无道理。资源模板（URI 带占位符）在智能体页不可挂载：它读不出来，勾上只会得到一条报错。

## 测试

```bash
dotnet test
```

当前 409 项测试，覆盖序列化、加密、存储 CRUD 与迁移、费用计算、错误映射、流式链路、工具循环、工具确认、会话搜索、会话内模型选择、自定义供应商、MCP 传输与内容浏览、多语言（语言包与代码/界面用到的键逐条比对）、界面布局。

其中三类值得单独说明：

- **本地 mock 服务端**（`MockOpenAiServer`）：测试通过真实的 OpenAI SDK 打到本机 HTTP 服务端，覆盖请求构造、SSE 解析、usage 采集、工具调用往返与错误状态码。只 mock `IChatClient` 会绕开最易出错的一层。
- **假 MCP 服务端**（`tools/FakeMcpServer`）：一个最小的 MCP stdio 服务端，由测试项目以 ProjectReference 引用，测试会真实启动它、走完 JSON-RPC 握手、发现工具并调用。MCP 最容易出错的就是 stdio 传输这一层，mock 掉服务接口恰好会把它整个绕过去。
- **无头布局测试**（`MainWindowLayoutTests`）：在 Avalonia Headless 下真实跑布局，断言输入区、底部工具行（模型/智能体/参数/发送）、状态栏等关键控件确实渲染在窗口范围内，且工具行与发送按钮不重叠。编译期绑定只能保证属性名没写错，保证不了元素没被挤出可视区域。
- **语言包一致性测试**（`LocalizationServiceTests` / `LocalizationUiTests`）：扫描 `src/Piable` 下所有 `.cs` 与 `.axaml`，把 `Loc.Get("…")` 与 `{DynamicResource Loc.…}` 用到的键与两份语言包比对，并检查带占位符的条目在两套语言里占位符一致；另有一个无头测试在真实窗口上切换语言，断言按钮文案当场变化。

### 已知的测试环境约束

`SqliteConnection.ClearAllPools()` 是**进程级**的，会连并行运行的其他测试正在使用的连接一起清掉。因此测试中创建 `PiableDatabase` 时显式关闭了连接池（`pooling: false`），让连接关闭即释放文件句柄。生产环境仍启用连接池。

**本地 mock 服务端全进程只起一个 `HttpListener`**，每个实例分到一个随机虚拟路径
（`http://127.0.0.1:{port}/{route}/chat/completions`），由服务端自己按路径分发，
断言看到的仍是去掉虚拟段后的 `/chat/completions`。

这么做是被代理坑出来的：设了 `HTTP_PROXY` 时 `HttpClient` 会把打到 `127.0.0.1` 的请求
也交给代理，实测请求会被投递到**别的端口**上的服务端——症状是"收到另一个测试准备的
响应""请求数翻倍"，失败集合每次都不同，很容易误判成并发问题。改成共用一个端口后，
代理就没有投错的机会了。

如果出于调试需要让每个实例各起一个监听器，务必先清掉代理：

```bash
unset HTTP_PROXY HTTPS_PROXY http_proxy https_proxy
```

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

1. 对话搜索与 PDF 导出（Markdown 导出已有）
2. 多语言资源文件
3. MCP resources 与 prompts
4. 工具调用的逐次确认（当前是"按智能体一次性授权"，粒度更细的确认能进一步降低误执行风险）
