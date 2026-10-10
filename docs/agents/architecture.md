# 当前架构与数据流

本文件描述现有生产代码，而非最初 MVP 的建议接口。修改前仍需阅读对应源码和测试。范围见[产品契约](product.md)，环境与命令见[开发指南](../development.md)。

## 分层与依赖

```text
Cuelify.Desktop → Cuelify.Infrastructure → Cuelify.Core
                      ↓
          FFmpeg / ONNX / HTTP / llama-server
```

Core 定义领域模型、接口和纯逻辑，不引用 Avalonia 或 Infrastructure。Infrastructure 实现 I/O、原生引擎和业务编排，不拥有窗口状态。Desktop 负责配置编辑、凭证解锁与会话状态、任务命令、页面及原生文件对话框，不另造识别或翻译流水线。

| 模块 | 主要入口与职责 |
| --- | --- |
| `Core/Media` | `ICommandRunner`、`IAudioProcessor`、`AudioChunkPlanner`：媒体契约及切点规划 |
| `Core/Speech` | 词与语音模型、`WordTimelineMerger`：局部时间转换及重叠去重 |
| `Core/Subtitles` | `CueBuilder`、`SrtSerializer`：分句、源文/严格译文序列化 |
| `Core/Translation` | `ITranslationEngine`、`PromptBuilder`、`AlignmentValidator`：模板和输出协议 |
| `Infrastructure/Media` | `ProcessCommandRunner`、`FfmpegAudioProcessor`：文本/二进制管道、音频准备和片段导出 |
| `Infrastructure/Speech` | Silero ONNX、ElevenLabs 客户端和词响应解析 |
| `Infrastructure/Transcription` | `TranscriptionPipeline`：识别缓存、分片并发、重试与原始 cue |
| `Infrastructure/Translation` | 云端 transport/provider、`TranslationOrchestrator`、提示词存储 |
| `Infrastructure/Translation/Local` | 预置模型目录、动态官方下载与校验、Vulkan 运行时、GPU 证据、采样与推理 |
| `Infrastructure/Storage` | `AtomicFile`：哈希、原子 JSON/文本读写；`CredentialStore`：跨平台主密码加密凭证 |
| `Desktop/Services` | `AppSettings`、`ConfigurationStore`、`DesktopJobService`、`WindowDialogs`、`UserErrorMessages` |
| `Desktop/ViewModels` | `MainWindowViewModel`：任务所有权、导航、步骤、命令与 UI 状态 |
| `Desktop/Views`、`Desktop/Design` | 窗口外壳、三个页面、语义 tokens、样式与共享矢量图标 |

目录均相对于 `src/` 下对应项目；详见 [DesktopJobService](../../src/Cuelify.Desktop/Services/DesktopJobService.cs)和 [MainWindowViewModel](../../src/Cuelify.Desktop/ViewModels/MainWindowViewModel.cs)。

## 识别流程

```text
选择文件 → ffprobe 检查
开始处理 → 输入指纹/作业锁 → 音频准备或缓存
         → 16 kHz 单声道 PCM → Silero VAD 或缓存
         → 静音优先分片 → 检查实际 WAV 大小
         → ElevenLabs 分片识别或缓存
         → 全局词时间/边界去重 → CueBuilder → 原文 cue
```

工作台的“下一步”只调用 Probe，不开始 VAD、ASR 或翻译。正式运行由 `DesktopJobService` 构造音频、VAD 和 ASR 适配器，复用 `TranscriptionPipeline`。VAD 签名包含模型哈希及检测参数；API Key 不参与缓存身份。

流水线对输入持有只读共享文件句柄，并用完整路径、长度、修改时间、内容 SHA-256 和准备版本建立指纹。音频缓存校验大小与哈希；VAD、分片和 ASR 参数参与各自缓存校验，成功分片单独保存。实际上传过大时有限缩短分片并重新导出，不重复上传同一过大文件。

词响应使用片段起点还原全局时间，再按时间与文本处理重叠。`CueBuilder` 完成分句；翻译层接收稳定 cue。Desktop 使用临时原文 SRT 承接识别编排，结束后清除临时输出，保留有效缓存；用户最终导出走严格译文序列化。

词解析和全局时间转换接受起止时间相同的词；原始词缓存不加人工时长。`CueBuilderOptions.ZeroDurationTolerance` 控制零时长文本的相邻合并及孤立字幕显示时长，默认 500 毫秒，桌面保存为 `ZeroDurationToleranceMs`。改变该值重新组装 cue，复用 ASR 分片缓存；翻译缓存仍由新 cue 内容和时间确定。

## 翻译流程

```text
原文 cue → 批次/上下文 → PromptBuilder → ITranslationEngine
        → AlignmentValidator → 部分接受/有限逐条补翻
        → 原子缓存 → 已校验结果进度 → 完整性判定 → SRT
```

- 兼容 API 与 DeepSeek 官方适配器复用 `ChatCompletionsTransport`，但请求能力映射分别处理。普通请求为非流式，只接收最终 `content`；思考文本不进入字幕。
- `PromptBuilder` 使用有限变量白名单，一次替换并正确处理 JSON；不执行模板表达式，也不再解释原文中的大括号。预设和输出格式位于 Core 的生产类型，不另写一套 UI 专用规则。
- 批量模板的批次受模板中条数与字符预算限制，单条模板固定请求一条。固定波次并发，前文取该波次开始时的稳定快照，避免完成顺序改变上下文和缓存身份。
- `PromptProfile.BatchTranslation` 是单条/批量模式的唯一来源，输出协议由其推导；`ITranslationEngine` 不再声明输出格式。兼容 API、DeepSeek 与 `LocalTranslationEngine` 都可返回纯文本或 JSON，由编排器按模板交给统一校验器。本地并发默认 2、线上默认 4，范围均为 1～8，请求粒度与模板一致。本地复用 llama-server，经流式 completion 检查 EOS、输出长度、取消和超时。
- `DesktopJobService` 以运行配置及模型/运行包文件版本判断是否可复用服务，版本只读取完整路径、大小及最后修改时间。同版本且 `/health` 可用时复用已有引擎，跳过运行程序能力检查与模型加载。冷启动时 `LlamaServerBinary` 检查帮助、版本、Vulkan 设备并核对运行包内容哈希；模型只持有读取句柄，不计算模型哈希或解析 GGUF metadata，不核对其架构/量化与预设。加载错误由 llama.cpp 报告，原始诊断经脱敏进入日志；媒体识别上传前完成本地就绪检查。`ModelFileVersion`、运行配置与运行包身份进入翻译缓存，协议版本为 `local-completion-file-version-v3`，保留旧缓存文件但不混用旧模型哈希身份。
- `LlamaServerSession` 用 `ProcessCommandRunner.CreateStartInfo` 创建无窗口进程，清除继承的 llama.cpp 参数覆盖；只监听 127.0.0.1，通过 `--port 0` 分配端口，关闭 Web UI 和浏览器跨域访问。指定 Vulkan 设备、GPU 卸载层数、slot 数及总上下文长度；禁用自动容量调整和共享 KV 池，核对 `/props` 的每 slot 上下文与容量。GPU 证据仅来自本次进程的模型加载日志，必须具备选定 Vulkan 设备、GPU 模型缓冲及非零层卸载。
- `PromptBuilder` 统一渲染前文（原文及已有译文）、后文（仅原文）与当前字幕，JSON 保留可读中文；默认前文 5 条、后文 2 条，共享字符预算。`TranslationRequest.PromptContext` 保留模板与参考条目，`EmbeddedPromptBudget.PrepareAsync` 经服务端 `/apply-template` 与 `/tokenize` 使用实际 tokenizer 逐条移除较远参考内容，不截断当前字幕或指令；参考全部移除仍超限时抛出 `PromptCapacityException`；编排器递归拆分批次，直到可执行或单条仍超限并明确失败，不截断/丢弃当前字幕。本地请求预览经同一裁剪和 chat template 返回实际提示词及 token 数；`/completion` 接收这份最终提示词，保证预算和预览与生成一致。
- 成功译文校验后持久化，每批结束发布 `TranslationProgress` 中的有效 cue 和失败 ID。桌面合并交错通知，不让晚到快照清掉已显示的成功译文。
- 定向重翻失效选中 ID 的旧缓存，恢复未选中结果；重翻失败或取消不能把该 ID 的旧成功译文重新当作本次结果。

缓存复用、对齐与实际云端参数应以[翻译编排](../../src/Cuelify.Infrastructure/Translation/TranslationOrchestrator.cs)和[云端选项](../../src/Cuelify.Infrastructure/Translation/CloudTranslationOptions.cs)为准；默认值不是服务商永远有效的 API 保证。

## 桌面状态与配置归属

`MainWindow` 是导航外壳，长期持有工作台、设置、日志三个 UserControl；它们共享窗口级 ViewModel。切页只改变可见页面，不重建 service、模型或作业。

| 状态 | 归属与用途 |
| --- | --- |
| `Page` / `Step` | 页面导航与三步位置；与业务进度分开 |
| `Settings`、提示词编辑字段 | 设置页草稿；编辑本身不改变已有结果 |
| `_savedSettings` | 最后成功保存的非敏感配置快照 |
| `TaskSourceLanguage`、`TaskTargetLanguage`、`TaskProviderIndex` | 工作台本次任务选项，默认值来自已保存设置；源语言同时映射识别代码与翻译名称 |
| `_jobSettings`、`_jobInputPath` | 实际任务启动时的配置和文件，结果预览、重翻及导出依据 |
| `LocalModelId`、`ModelFiles`、`ModelPath`、`ModelSha256` | 选中预设、各模型路径及下载来源哈希、当前路径及下载哈希；手动选择清空哈希，历史哈希不参与运行前校验或缓存身份；保存后进入任务快照 |
| `PromptLibrary` / `ModelPromptBindings` | 自定义模板库、模型到模板 ID 的关联；内置模板在 Core 中提供，删除时移除对该模板的显式关联，各模型回退自己的默认模板，关联与删除通过同一配置原子保存 |
| `SelectedPrompt` / 提示词编辑字段 | 独立编辑对象与草稿，不随模型切换；列表筛选选择与实际编辑对象分开，未保存切换先恢复原高亮并等待模态保存/放弃/取消，保存失败保持原草稿 |
| `Rows` / `SelectedCue` | 字幕和选择；区分等待、翻译中、失败、取消，原文和时间保持稳定 |
| `IsBusy` / `_isMediaJob` / cancellation | 串行保护长操作，区分媒体处理与服务测试 |
| `IsPreparing` / `PreparationStatus` / `PreparationElapsed` | 本次准备或服务测试的真实阶段与计时；工作台/设置复用固定状态视图，阶段通过 UI dispatcher 更新，完成/取消/关闭后停止计时并拒绝陈旧通知 |
| `ElevenLabsKey` / `TranslationKey` | 解锁后的编辑字段，翻译字段按当前设置 provider 映射；非 AppSettings 成员 |
| `CredentialStore` / `IsCredentialBusy` | 加密文件、会话派生密钥、三个服务凭证和串行凭证操作；启动默认锁定 |

任务启动从已保存配置复制，再覆盖本次选项。保存设置只更新默认配置，未保存的修改不进入媒体处理请求。服务测试是设置页的显式动作，校验并使用当前设置草稿；提示词只校验当前引擎和所选模型，未选模板不能阻止当前服务测试。保存与加载配置仍完整校验所有模板，错误标明模板名称；测试不改变已完成任务状态或导出资格。

`EmbeddedModel.DefaultPromptId` 只保存默认模板 ID：1.8B 使用简单模板，7B 使用上下文模板；线上默认通用批量模板。`PromptLibrary` 保存自定义模板完整内容，`ModelPromptBindings` 保存引用和模型显示名。本地关联键使用模型 ID，线上使用 provider、规范化实际端点与模型名的非敏感哈希。共享模板更新后新任务使用新内容；已启动任务深拷贝整个库和关联，保证预览/重翻仍使用原正文。不存在的引用和重复模板名称在保存前拒绝。内置模板不允许覆盖，副本使用独立 ID。

`MainWindowViewModel.Inference` 投影当前引擎的并发数和线上可空的输出 token 数，配置继续分别存储 `LocalConcurrency`/`TranslationConcurrency`、`LocalMaximumTokens`/`MaximumTokens`，不改变 schema 或默认值；模型上下文容量减少时缩小超过半容量的本地输出值。准备流程通过 `LocalPreparationProgress` 从服务工厂、实际 llama-server 启动会话及测试请求上报阶段，时间戳用于阶段日志；UI dispatcher 排空当前通知后才移交识别或最终测试状态。阶段包括运行程序检查、进程启动、模型加载、服务检查、就绪及复用服务，不再包含模型校验。下载完整性校验只在下载流程执行。

`SpeechLanguages` 维护 Scribe v2 官方语言名称与 ISO 639-1/639-3 映射。界面只显示中文源语言名称，区域标识兼容输入会转为语言代码；新版以显示语言统一对应识别代码；已有匹配的合法两位/三位代码可保留，不迁移旧 JSON。未选定源语言时省略 `language_code`。未知语言在上传前拒绝。

日志通过 Avalonia `SelectionModel<string>` 按行索引选择，复制命令按原顺序拼接完整条目，避免相同文本的未选行被误复制；`WindowDialogs` 封装窗口剪贴板写入。

单条重翻和请求预览使用原任务配置；导出命名使用原任务目标语言。更换输入清除旧任务和结果；新建任务回到第一步并载入已保存默认值。调整选项后重新处理才生成另一份配置对应的结果。配置正确且初始化完成后，命令按状态启用；处理中允许导航，相关编辑和步骤切换锁定。

## 缓存与敏感信息

用户数据根为 `%LOCALAPPDATA%\Cuelify`：

| 路径 | 内容 |
| --- | --- |
| `settings.json` | 非敏感设置、自定义提示词库和模型引用 |
| `credentials.json` / `credentials.lock` | 主密码加密凭证；后者为修改时的排他文件句柄，不包含秘密 |
| `Jobs/<job-id>/` | 输入/音频/VAD/分片/识别记录及状态 |
| `Translations/<identity>/` | 翻译批次、结果及状态 |
| `Working/` | 处理期间的临时输出 |
| `llama.cpp/<tag>-<digest-prefix>/` | 独立版本的官方 Vulkan 运行包、对应 LICENSE 与 installation.json；不同版本不原地覆盖 |
| `llama.cpp/package.zip.partial` / `.partial.json` / `package.download.lock` | 运行包片段、当次官方元数据和排他下载句柄 |
| `<model-id>.gguf` | 本地模型默认文件；与配置文件同级，远端文件名变化不改变本地默认路径 |
| `<model-id>.gguf.partial` / `.partial.json` / `.download.lock` | 下载片段、本次官方元数据和下载排他句柄；取消保留可续传片段 |

识别与翻译缓存身份分开；翻译身份包含引擎非敏感签名、模板正文、批量方式及生效批次参数、语言、风格和原始 cue，批次请求还包含实际上下文。模板 ID、名称与说明不参与生成身份，仅重命名/改备注不失效缓存。API Key 不进入身份。配置拒绝高级 JSON 中受控或嵌套凭据字段，以及误贴的当前会话 Key。

原子写保护完整性，作业文件句柄提供互斥；锁文件仍在不表示作业仍被占用。缓存包含音频、原文、译文，不能当作可公开测试 fixture。迁移缓存格式要考虑身份变化、旧数据校验和重新计费影响。

`ModelDownloadService` 从官方 API 动态发现指定量化的唯一 GGUF，不冻结远端版本、文件名、大小或哈希。续传只复用同一资产的片段，校验 Range；服务器忽略 Range 时重写片段。哈希与大小采用本次官方提供的信息，完整文件以原子移动替换，旧正式文件在下载失败时保留。手动选择文件动态核对官方当前资产；已保存模型使用已记录内容哈希离线校验，文件名不参与身份。

`ConfigurationStore` 根据自身 Root 生成默认路径。settings.json schema 2 不迁移旧设置：读取缺失/较旧 schema 时备份为同目录 `settings.previous-<id>.json`，原子写入新版默认配置并提示重新配置；未来版本及损坏 JSON 报错且不覆盖。独立凭证、模型、运行包与缓存文件不清理。模型切换仅恢复各自路径/哈希及模板关联，不触碰独立编辑草稿。模型、采样与运行包身份进入引擎缓存签名；更换模板本身不重启 llama-server。

下载仍只修改设置草稿，保存后用于新任务；下载前释放闲置服务，处理中禁止下载或改模型，关闭等待推理再释放。GGUF 与运行包共用 `ResumableDownload`，保持动态修订、Range、取消和安全解压；独立运行包版本不原地覆盖。

模型下拉仅提供两个预置模型，使用稳定 `ObservableCollection`，刷新路径和哈希不重建 ItemsSource。提示词列表按 ID 增删/替换条目，筛选不丢失编辑草稿，清除筛选可恢复选择。模型切换期间拒绝重复回写，配置更新结束后通知选择状态，避免双向绑定把临时选择再次写入配置。

## 主密码凭证存储

`CredentialStore` 位于 Infrastructure，只使用 .NET 托管密码学及文件 API。应用数据根沿用 `ConfigurationStore.Root`，由 `Environment.SpecialFolder.LocalApplicationData` 定位；当前 Windows 路径为上述目录，不把该路径写死到凭证服务。

版本 1 使用 PBKDF2-SHA256（600,000 次、16 字节随机盐、32 字节派生密钥）及 AES-256-GCM（12 字节随机 nonce、16 字节标签）。版本、算法、迭代数和盐纳入认证附加数据；文件大小、字段长度和迭代数有读取上限。每次加密使用新的 nonce，更换主密码生成新的盐。密钥和明文序列化缓冲区尽可能清零，托管字符串只在必要会话内保留，不承诺运行时字符串的物理擦除。

凭证原子写入独立 JSON，临时文件只包含密文。操作用信号量及 `credentials.lock` 排他句柄串行保护；保存和改密前校验文件修订，拒绝陈旧窗口覆盖新凭证。解锁只有通过完整解密、认证及结构校验才建立会话；错误密码或篡改均不改变原文件。

`MainWindowViewModel.Credentials` 负责状态与命令，主密码只在独立对话框短暂输入，退出对话框清空字段。密钥不放入任务配置快照；真实调用按任务快照 provider 获取对应会话密钥，重翻不受当前设置页引擎影响。兼容服务密钥关联规范化地址，更换地址后需确认或重填，任务仍依据已保存服务地址判定。非敏感配置保存和日志脱敏检查全部服务密钥。

第一步及步骤导航校验主密码与 ElevenLabs 密钥，运行校验任务引擎所需凭证；云端测试使用设置草稿，重翻使用原任务服务。锁定不清空字幕，导出不依赖凭证。所有凭证编辑操作在业务处理期间禁用，凭证操作期间阻止启动业务；关闭取消并等待凭证操作后释放会话。没有自动锁定计时器或环境变量回退。

## 线程、取消与释放

桌面服务将 CPU/原生重操作放到后台，进度经 Dispatcher 更新 UI。HTTP、Process 和推理共享取消传播；旧操作完成后的延迟通知不能污染新任务。重入由命令与业务状态共同保护。

关闭窗口先请求取消，等待当前操作，再释放本地服务进程、HTTP 和会话凭据，最后排队关闭窗口，避免同步释放时重入 Closing。配置改变使本地引擎身份变化时，先结束旧服务再创建新实例。单条取消关闭对应流式连接，由服务撤销任务并归还 slot；进程异常退出不沿用旧 GPU 证据。Windows Job Object 随父进程句柄回收，防止应用异常结束后遗留服务。窗口事件订阅需有对应取消订阅，不能因导航增加多份回调。

## 当前实现的明确限制

云端地址、模型及生成参数目前由兼容接口与 DeepSeek 共用一组配置字段；提示词库与模型关联已独立，密钥已分为 ElevenLabs、兼容服务和 DeepSeek 三个独立槽位，翻译页按引擎显示对应输入。工作台可选 provider，但不代表已实现多个服务配置档案或多账户凭据管理。以后扩展这些能力需要独立计划并测试配置隔离，不能在交接中声称已经具备。

运行日志保留最近 200 条；没有多账户管理、播放器或编辑器。真实服务/GPU证据与人工验收边界见[验证状态](status.md)。
