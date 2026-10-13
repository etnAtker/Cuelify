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
| `Core/Projects` | `ProjectDocument`、`MediaTaskDocument`、`StageRecord`：项目成员、阶段状态与版本依赖 |
| `Core/Media` | `ICommandRunner`、`IAudioProcessor`、`AudioChunkPlanner`：媒体契约及切点规划 |
| `Core/Speech` | 词与语音模型、`WordTimelineMerger`：局部时间转换及重叠去重 |
| `Core/Subtitles` | `CueBuilder`、`SrtSerializer`：分句、源文/严格译文序列化 |
| `Core/Translation` | `SubtitlePipelineProfile`、`PipelinePromptBuilder`、`PipelineDisplay`、`PromptBuilder`、`AlignmentValidator`：管线、词范围、显示与输出协议 |
| `Infrastructure/Media` | `ProcessCommandRunner`、`FfmpegAudioProcessor`：文本/二进制管道、音频准备和片段导出 |
| `Infrastructure/Speech` | Silero ONNX、ElevenLabs 客户端和词响应解析 |
| `Infrastructure/Transcription` | `ChunkingPipeline`：PCM/VAD/分片与实际 WAV 校验；`TranscriptionPipeline`：独立消费分片、识别缓存、分片并发与原始词/cue |
| `Infrastructure/Translation` | 云端 transport/provider、`SubtitlePipelineOrchestrator`、`TranslationOrchestrator`：阶段缓存与补译 |
| `Infrastructure/Translation/Local` | 预置模型目录、动态官方下载与校验、Vulkan 运行时、GPU 证据、采样与推理 |
| `Infrastructure/Storage` | `ProjectStore`：项目目录/清单扫描、任务恢复、路径边界与排他锁；`AtomicFile`：哈希、原子 JSON/文本读写；`CredentialStore`：跨平台主密码加密凭证 |
| `Desktop/Services` | `AppSettings`、`ConfigurationStore`、`ProjectTaskService`、`DesktopJobService`、`WindowDialogs`、`UserErrorMessages` |
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
         → 全局词时间/边界去重 → 原始词结果
```

添加媒体只调用 Probe，不开始 VAD、ASR 或翻译。项目执行由 ProjectTaskService 按 TaskStagePlan 调用 DesktopJobService：ChunkingPipeline 生成持久化 ChunkingResult，TranscriptionPipeline.TranscribePreparedAsync 独立消费它。ASR 重试不加载 VAD、不再次导出有效 WAV，也不要求原媒体仍存在。旧 RunAsync 组合接口保留兼容；VAD 签名包含模型哈希及参数，API Key 不参与缓存身份。

流水线对输入持有只读共享文件句柄，并用完整路径、长度、修改时间、内容 SHA-256 和准备版本建立指纹。音频缓存校验大小与哈希；VAD、分片和 ASR 参数参与各自缓存校验，成功分片单独保存。实际上传过大时有限缩短分片并重新导出，不重复上传同一过大文件。

词响应还原全局时间并去重，原始 Words 作为识别词中间结果，项目 ASR 不生成字幕。CueBuilder.BuildWithRanges 在独立程序分句步骤生成 cue 和连续词范围；模型分句生成同构 SubtitlePipelineResult。ValidateSource 验证 cue/range 对齐与连续完整覆盖。用户只导出严格译文 SRT。

词解析和全局时间转换接受起止时间相同的词；原始词缓存不加人工时长。`CueBuilderOptions.ZeroDurationTolerance` 控制零时长文本的相邻合并及孤立字幕显示时长，默认 500 毫秒，桌面保存为 `ZeroDurationToleranceMs`。改变该值重新组装 cue，复用 ASR 分片缓存；翻译缓存仍由新 cue 内容和时间确定。

## 字幕管线

TaskStagePlan 固定分片→识别词→分句→结果，与分句方法、模型及提供商无关。ProgramSegmentation 复用 CueBuilder，并保留零时长词处理；SubtitlePipelineOrchestrator 的模型入口只执行 SourceTexts，程序分句分支复用 ProgramSegmentation。两者均输出纯原文 SubtitlePipelineResult（Cues/Ranges），结果步骤只读取该结果并调用 TranslationOrchestrator；移除所有从 ASR 直接联合生成结果的执行分支。

`PipelinePromptBuilder` 按原词时间规划窗口，各起点允许所有满足最长字幕时间的末词 ID，没有标点、助词、说话人或固定停顿的硬筛选。新请求附连贯原文、以块起点为原点的紧凑时间词表和参考前后文，模型只输出 texts 原文数组。GenerationOutputContract 保存该块的原词文字与全部合法范围，ParseRanges 逐字映射连续词 ID 并验收时间；本地 OutputGrammarCompiler 将同一图的每条边编译为对应原文 JSON 字符串，Core 不携带 GBNF。云端同样验收，失败附具体原因并有限重试。WordEnds 的执行、提示词、解析、数字 GBNF 与拆块恢复已删除。PipelineSettingsMigration 从原始 JSON 识别格式 0 或缺失格式的旧模型分句；显示配置可投影为现行类型，但原始版本快照不改写。StageExecutionPolicy 阻止未完成旧分句继续/失败重试，重新执行创建新版本；完整旧结果仍作为固定字幕供翻译及导出使用。

PipelineDisplay 从模型选择的连续词范围计算纯原文 cue，合并重叠/同起点文字，在可用静音内延长显示并生成最终 ID。ProgramSegmentation 保留 CueBuilder 对零时长词的处理，再在可用静音内满足最短显示目标。原词时码不修改；最终 cue 在分句版本中固定，翻译只替换译文并按两行目标换行，不再以译文长度调整时间。

新分句采用 `segmentation-texts-v1` 缓存身份，每个成功请求独立原子保存完整响应。块间按依赖顺序执行：除末块或单条响应外，确认前面各句，将最后一句连同后文再次请求；容量不足先缩小当前请求并保存 `.capacity.json`，未处理后半随暂存末句进入后续窗口。恢复从原词起点重新验收成功响应，确定相同的确认范围和暂存尾句，成功请求不重复发送。动态剩余工作单元使界面区分继续和失败重试，取消/失败不将暂存词计为完成；单词请求仍超限则明确失败。翻译请求并发不变，模型 slot 配置仍按既有模型档案分配。

SegmentationIdentity 与 TranslationIdentity 分别描述本步参数，分句身份包含输出格式而不包含翻译提示词、批次、行宽和阅读速度；翻译身份不包含分句提示词、候选规划或分块参数。名称、说明、ID 不影响执行身份。旧 `segmentation-v3` 缓存文件和既有结果保留，旧请求缓存不参与 SourceTexts 执行。现行格式保留编号 1 和既有请求/阶段缓存身份；格式 0 不再是运行格式。

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
- `DesktopJobService` 按运行配置及模型/运行包文件版本复用服务；文件版本只读取完整路径、大小及修改时间。同版本且 `/health` 可用时复用引擎，冷启动检查运行包能力、Vulkan 设备与运行包内容哈希。模型只持有读取句柄，不计算内容哈希或解析 GGUF；加载错误由 llama.cpp 报告并脱敏记录。在需要模型的步骤执行前检查本地就绪。运行配置与文件版本进入翻译缓存，协议为 `local-pipeline-completion-v4`；保留旧缓存但不混用旧身份。
- `LlamaServerSession` 用 `ProcessCommandRunner.CreateStartInfo` 创建无窗口进程，清除继承的 llama.cpp 参数覆盖；只监听 127.0.0.1，通过 `--port 0` 分配端口，关闭 Web UI 和浏览器跨域访问。指定 Vulkan 设备、GPU 卸载层数、slot 数及总上下文长度；禁用自动容量调整和共享 KV 池，核对 `/props` 的每 slot 上下文与容量。GPU 证据仅来自本次进程的模型加载日志，必须具备选定 Vulkan 设备、GPU 模型缓冲及非零层卸载。
- `PromptBuilder` 统一渲染前文（原文及已有译文）、后文（仅原文）与当前字幕，JSON 保留可读中文；默认前文 5 条、后文 2 条，共享字符预算。`TranslationRequest.PromptContext` 保留模板与参考条目，`EmbeddedPromptBudget.PrepareAsync` 经服务端 `/apply-template` 与 `/tokenize` 使用实际 tokenizer 逐条移除较远参考内容，不截断当前字幕或指令；参考全部移除仍超限时抛出 `PromptCapacityException`；编排器递归拆分批次，直到可执行或单条仍超限并明确失败，不截断/丢弃当前字幕。本地请求预览经同一裁剪和 chat template 返回实际提示词及 token 数；`/completion` 接收这份最终提示词，保证预算和预览与生成一致。
- 成功译文校验后持久化，每批结束发布 `TranslationProgress` 中的有效 cue 和失败 ID。桌面合并交错通知，不让晚到快照清掉已显示的成功译文。
- `AlignmentValidator` 统一校验非空文本、允许的控制字符、纯文本/JSON 协议和 ID 对齐；不比较原译文是否相同，也不依赖源目标语言相同的豁免。服务响应、成功缓存和定向重翻恢复旧结果使用同一内容校验，缓存身份及 schema 保持不变，规则决定见 D019。
- 定向重翻失效选中 ID 的旧缓存，恢复未选中结果；重翻失败或取消不能把该 ID 的旧成功译文重新当作本次结果。

缓存复用、对齐与实际云端参数应以[翻译编排](../../src/Cuelify.Infrastructure/Translation/TranslationOrchestrator.cs)和[云端选项](../../src/Cuelify.Infrastructure/Translation/CloudTranslationOptions.cs)为准；默认值不是服务商永远有效的 API 保证。

## 桌面状态与配置归属

StageExecutionPolicy 共用步骤快照解析、模型/云端需求、未完成与失败步骤选择及旧分句准入；单媒体、步骤命令和批量执行使用同一 RunMediaCoreAsync/RunBatchCoreAsync。兼容命令仅转发，批量范围和取消状态统一持久化。批量凭证准入按本次实际快照和步骤模型判断，纯本地分句/翻译不解锁凭证。

StageResultReader 统一当前结果与历史预览的缺失/损坏/恢复读取。SubtitleRowsViewModel 分别拥有当前结果与历史预览的行和选择，共用投影、选择保持和完整性判定；TranslationResult.MergeAccepted 只合并相同 ID、原文及时码的成功通知。新结果初始化显式替换，避免重翻重新带入所选旧译文。SubtitleExportService 共用版本冻结、文件名、路径保护、覆盖确认与原子导出。

模型字段由 ModelProfile 的本地/云端字段集维护；阶段捕获使用同一执行字段集，包含本地超时。旧快照缺少超时时仍使用兼容默认值，不能从当前全局配置反推历史值。DesktopJobService 共用分片/VAD/ASR 参数构造及翻译结果换行；RetryDelay 统一延迟上限与 Retry-After 判断，识别/翻译指数退避和分句固定延迟的既有策略保留。ProjectTaskService 收尾先确定失败/取消状态，再等待部分结果写入；分别尝试阶段记录和 task 保存，多个错误保留原始异常，不用写入失败绕过状态收尾。


`OperationProgressViewModel` 管理一次显示操作的阶段、计数、用时、取消状态和通知序号；`ProgressStatusView` 与共享 `operation-progress` 样式供步骤、准备、下载、凭证和结果加载复用。主窗口 Activity 保留跨页面活动，后台媒体的进度只投影到匹配媒体/步骤/版本，历史读取使用独立局部等待且不覆盖后台活动。阶段未知总量使用等待，短 I/O 延迟显示；结束停止计时器，旧显示作用域不能关闭或修改新操作。StageWorkProgress 为非持久化进度事件，使用 long 字节/工作量及单位；配置和任务 schema 不变。

ChunkingPipeline 将媒体/PCM 哈希、FFmpeg PCM 输出、Silero 已分析字节和已校验导出分片分别上报。ProgressStream 观察成功读写且不拥有底层流，回调限频；原有 SHA-256 与二进制处理契约不变。ElevenLabs 上报上传、等待响应及校验阶段，ASR 汇总仍以完成分片计算。TranslationOrchestrator 从已验收译文及本次处理 ID 范围汇总成功/处理中/失败/等待，容量拆批和重试不会增加分母；ProjectTaskService 透传统一计量。下载与完整性校验共用字节进度，运行包解压发布文件计数。导入逐个原子保存项目成员，取消不丢失先前成功添加项。

`MainWindow` 是导航外壳，长期持有工作台、设置、日志三个 UserControl；它们共享窗口级 ViewModel。切页只改变可见页面，不重建 service、模型或作业。

| 状态 | 归属与用途 |
| --- | --- |
| `Page` / `Workspace.SelectedStep` | 项目列表、项目详情、设置、日志导航；步骤随媒体当前管线定义，与处理进度分开 |
| `Settings`、提示词编辑字段 | 设置页草稿；编辑本身不改变已有结果 |
| `_savedSettings` | 最后成功保存的非敏感配置快照 |
| 媒体 `Options` / `ModelOverrides` | 媒体管线和语言草稿，及按步骤的模型覆盖；执行前捕获快照 |
| `_jobSettings`、`_jobInputPath` | 实际任务启动时的配置和文件，结果预览、重翻及导出依据 |
| `LocalModelId`、`ModelFiles`、`ModelPath`、`ModelSha256` | 选中预设、各模型路径及下载来源哈希、当前路径及下载哈希；手动选择清空哈希，历史哈希不参与运行前校验或缓存身份；保存后进入任务快照 |
| `PipelineLibrary` / `Models` / `PipelineModelDefaults` | 自定义管线、模型服务/推理档案与管线步骤默认模型；模型不包含默认管线 |
| `SelectedPipeline` / 步骤编辑字段 | 独立草稿，不随模型切换；包含分句、翻译提示词及参数，阶段切换仅导航；未保存管线切换提供保存/放弃/取消 |
| `Rows` / `CueSelection` | 字幕及 `SelectionModel<CueRow>` 多选；区分等待、翻译中、失败、取消，原文和时间保持稳定；行替换后按 ID 恢复选择，新任务清空选择 |
| `IsBusy` / `_isMediaJob` / cancellation | 串行保护长操作，区分媒体处理与服务测试 |
| `IsPreparing` / `PreparationStatus` / `PreparationElapsed` | 本次准备或服务测试的真实阶段与计时；工作台/设置复用固定状态视图，阶段通过 UI dispatcher 更新，完成/取消/关闭后停止计时并拒绝陈旧通知 |
| `ElevenLabsKey` / `TranslationKey` | 解锁后的编辑字段，翻译字段按当前设置 provider 映射；非 AppSettings 成员 |
| `CredentialStore` / `IsCredentialBusy` | 加密文件、会话派生密钥、三个服务凭证和串行凭证操作；启动默认锁定 |

ProjectWorkspaceViewModel 管理实际步骤、每步预览版本、内容与直接执行的步骤按钮。MediaSettings 取媒体快照并合并当前模型注册与资产；ForStep 按批量冻结模型→媒体步骤覆盖→管线默认模型解析。执行时捕获完整配置；继续/失败重试始终沿用原版本配置与直接上游，重新执行创建新版本。新媒体捕获已保存的全局默认设置；项目 DefaultSettings 仅用于兼容缺少选项的历史媒体，不再编辑或用于导入。模型准备发生在需要模型的步骤内，准备失败写入该版本失败状态。

ModelProfile 只捕获服务、推理与资产参数，排除管线、媒体选项及密钥。Models 以稳定模型 ID 保存配置档案；PipelineModelDefaults 支持内置与自定义管线的分句/结果默认模型。媒体 ModelOverrides 按 TaskStage 独立覆盖。ASR 步骤可独立设置现有 ElevenLabs 模型。采样覆盖属于管线；Index 本地默认 greedy，Hy 保留现有采样，llama-server 使用 Jinja 与 enable_thinking=false。

`MainWindowViewModel.Inference` 投影当前引擎的并发数和线上可空的输出 token 数，配置继续分别存储 `LocalConcurrency`/`TranslationConcurrency`、`LocalMaximumTokens`/`MaximumTokens`，仍复用原并发与默认输出字段；阶段采样覆盖另归管线；模型上下文容量减少时缩小超过半容量的本地输出值。准备流程通过 `LocalPreparationProgress` 从服务工厂、实际 llama-server 启动会话及测试请求上报阶段，时间戳用于阶段日志；UI dispatcher 排空当前通知后才移交识别或最终测试状态。阶段包括运行程序检查、进程启动、模型加载、服务检查、就绪及复用服务，不再包含模型校验。下载完整性校验只在下载流程执行。

`SpeechLanguages` 维护 Scribe v2 官方语言名称与 ISO 639-1/639-3 映射。界面只显示中文源语言名称，区域标识兼容输入会转为语言代码；新版以显示语言统一对应识别代码；已有匹配的合法两位/三位代码可保留，不迁移旧 JSON。未选定源语言时省略 `language_code`。未知语言在上传前拒绝。

日志通过 Avalonia `SelectionModel<string>` 按行索引选择，复制命令按原顺序拼接完整条目，避免相同文本的未选行被误复制；`WindowDialogs` 封装窗口剪贴板写入。

结果版本携带源词范围及固定 cue，直接上游永远为 Subtitles。重翻创建 Operation=Retranslate 新版本，记录 BaseResultVersionId，使用当前结果步骤模型并强制清除所选译文；继续/失败重试读取本版本快照。StageSettingsSnapshot 仅保存本步字段、单一执行管线定义及已解析的模型参数/引用，隔离未使用步骤和其他提供商配置。导出使用所预览版本语言快照。旧单媒体重翻与请求预览同样使用结果步骤快照。

## 缓存与敏感信息

用户数据根为 `%LOCALAPPDATA%\Cuelify`：

| 路径 | 内容 |
| --- | --- |
| `Projects/<project-id>/project.json` | schema 1 项目名、默认配置快照、媒体 ID 顺序及时间；项目列表由扫描重建 |
| `Projects/<project-id>/Media/<media-id>/task.json` | schema 3 输入路径、媒体选项、步骤模型覆盖及当前 StageRecord 指针 |
| `Projects/<project-id>/Media/<media-id>/Results/<stage>/<version-id>/` | result.json、stage.json 依赖描述及本步骤的 WAV/请求缓存 |
| `workspace.json` | 最近打开的项目和媒体 ID，无业务状态 |
| `settings.json` | schema 5 非敏感设置、模型档案、自定义管线、步骤默认模型及旧联合提示词档案 |
| `credentials.json` / `credentials.lock` | 主密码加密凭证；后者为修改时的排他文件句柄，不包含秘密 |
| `Jobs/<job-id>/` | 输入/音频/VAD/分片/识别记录及状态 |
| Pipelines/<identity>/ | 模型分句逐块接受响应及最终 cue/词范围，取消保留成功块 |
| Translations/<identity>/ | 翻译批次、结果及状态 |
| `Working/` | 处理期间的临时输出 |
| `llama.cpp/<tag>-<digest-prefix>/` | 独立版本的官方 Vulkan 运行包、对应 LICENSE 与 installation.json；不同版本不原地覆盖 |
| `llama.cpp/package.zip.partial` / `.partial.json` / `package.download.lock` | 运行包片段、当次官方元数据和排他下载句柄 |
| `<model-id>.gguf` | 本地模型默认文件；与配置文件同级，远端文件名变化不改变本地默认路径 |
| `<model-id>.gguf.partial` / `.partial.json` / `.download.lock` | 下载片段、本次来源元数据和下载排他句柄；取消保留可续传片段 |

ASR、分句与翻译分别缓存。分句身份含引擎、原始词、时长、分句参数及提示词使用的语言；块身份含实际消息、采样和输出契约。翻译身份含最终 cue、本步模板/采样/批次/上下文与语言风格，请求身份含实际消息、输入和契约。ModelProfile.ExecutionParameters 只取当前提供商的参数。API Key 不进入身份，读取时重新校验，成功结果原子保存，锁文件防止并写；旧身份缓存保留，不假设旧协议可直接套用新请求。

原子写保护完整性，作业文件句柄提供互斥；锁文件仍在不表示作业仍被占用。缓存包含音频、原文、译文，不能当作可公开测试 fixture。迁移缓存格式要考虑身份变化、旧数据校验和重新计费影响。

`ModelDownloadService` 动态发现各模型来源仓库的唯一指定量化 GGUF（排除视觉投影文件）；续传仅复用同资产片段，校验 Range、大小、GGUF 文件头与可用 SHA 后原子替换，失败保留旧文件。手动文件不联网核对官方身份，也不计算内容哈希，延续 D018。

ConfigurationStore 使用 Root 生成默认路径，将 schema 3/4 备份后迁移为 5；旧联合内置 ID 映射到模型分句，旧自定义联合提示词存入 ArchivedPipelines，活跃分句提示词改用安全独立模板。更早配置备份后重建。ProjectStore 先将 task schema 1 的明确路径迁为版本引用，再备份升级到 3。旧联合结果仅在连续词覆盖、原文和词时间均可核实时导入新分句/结果版本（ImportedFromVersionId），保留旧 cue 和时码及原文件；无法导入的结果原文件保留，不提供专用浏览入口，也不进入当前候选。迁移可重复执行，未来格式或损坏 JSON 不覆盖，凭证、资产和缓存保留。

下载仍只修改设置草稿，保存后用于新任务；下载前释放闲置服务，处理中禁止下载或改模型，关闭等待推理再释放。GGUF 与运行包共用 `ResumableDownload`，保持动态修订、Range、取消和安全解压；独立运行包版本不原地覆盖。

模型下拉提供四个预设并保持稳定集合；管线按 ID 更新，筛选不丢草稿，模型切换拒绝重复回写。

## 主密码凭证存储

`CredentialStore` 位于 Infrastructure，只使用 .NET 托管密码学及文件 API。应用数据根沿用 `ConfigurationStore.Root`，由 `Environment.SpecialFolder.LocalApplicationData` 定位；当前 Windows 路径为上述目录，不把该路径写死到凭证服务。

版本 1 使用 PBKDF2-SHA256（600,000 次、16 字节随机盐、32 字节派生密钥）及 AES-256-GCM（12 字节随机 nonce、16 字节标签）。版本、算法、迭代数和盐纳入认证附加数据；文件大小、字段长度和迭代数有读取上限。每次加密使用新的 nonce，更换主密码生成新的盐。密钥和明文序列化缓冲区尽可能清零，托管字符串只在必要会话内保留，不承诺运行时字符串的物理擦除。

凭证原子写入独立 JSON，临时文件只包含密文。操作用信号量及 `credentials.lock` 排他句柄串行保护；保存和改密前校验文件修订，拒绝陈旧窗口覆盖新凭证。解锁只有通过完整解密、认证及结构校验才建立会话；错误密码或篡改均不改变原文件。

`MainWindowViewModel.Credentials` 负责状态与命令，主密码只在独立对话框短暂输入，退出对话框清空字段。密钥不放入任务配置快照；真实调用按任务快照 provider 获取对应会话密钥，重翻不受当前设置页引擎影响。兼容服务密钥关联规范化地址，更换地址后需确认或重填，任务仍依据已保存服务地址判定。非敏感配置保存和日志脱敏检查全部服务密钥。

导航和历史查看不要求凭证；运行按步骤和快照检查所需密钥，云端测试使用设置草稿。锁定不清空字幕，导出不依赖凭证。凭证编辑在处理中禁用，凭证操作时阻止启动处理；输入框失焦及保存设置均通过串行草稿保存链加密落盘；保存失败保留输入。锁定等待保存，关闭等待已触发的保存后释放会话。没有自动锁定或环境变量回退。

## 线程、取消与释放

桌面服务将 CPU/原生重操作放到后台，进度经 Dispatcher 更新 UI。HTTP、Process 和推理共享取消传播；旧操作完成后的延迟通知不能污染新任务。重入由命令与业务状态共同保护。

关闭窗口先请求取消，等待当前操作，再释放本地服务进程、HTTP 和会话凭据，最后排队关闭窗口，避免同步释放时重入 Closing。配置改变使本地引擎身份变化时，先结束旧服务再创建新实例。单条取消关闭对应流式连接，由服务撤销任务并归还 slot；进程异常退出不沿用旧 GPU 证据。Windows Job Object 随父进程句柄回收，防止应用异常结束后遗留服务。窗口事件订阅需有对应取消订阅，不能因导航增加多份回调。

## 项目执行与依赖版本

ProjectTaskService 复用既有生产编排；State 是执行事实，NeedsUpdate/ResultUnavailable 是独立可用性标记。每个 StageRecord 首次执行捕获 Version、CreatedAt、UpstreamStage、UpstreamVersionId、Operation、配置快照与结果路径。依赖以步骤和版本 ID 为准；内容哈希仅用于缓存。完成版本不会被继续处理覆盖。

WorkPlanKnown/UnitStates 记录实际处理覆盖，拆块纳入子块计划。Complete 继续未完成版本；RetryFailed 在无未处理单元时处理记录的失败单元；Restart 建新版本。ProjectStore 按固定直接上游 ID 级联选最新 CreatedAt 下游（包括失败），无匹配清空指针，历史保留。SetCurrent 同样级联；VersionsAsync 当前列表只显示匹配父版本的候选。切换分句方式不改变依赖链或 Tab，程序/模型分句版本可互相选用；旧联合档案不得进入新链。

ProjectStore 校验 GUID、schema、阶段状态与相对结果路径，拒绝越出媒体目录。持有项目排他锁后恢复 Running 为 Interrupted；未来格式/损坏清单提示错误并保留原文件。窗口打开项目只读已有结果，不自动发起付费操作。项目内顺序遍历保存的媒体列表，普通媒体错误记录并继续，取消/凭证错误停止。

## 当前实现的明确限制

云端设置页仍以当前兼容/DeepSeek 服务字段编辑模型档案；保存后 Models 保存所选服务档案，媒体步骤可以独立选择预置 DeepSeek、已配置服务档案及四个本地预设；模型列表与密钥状态无关，保存服务设置后立即刷新工作台与管线模型列表。密钥仍仅三个服务槽位，不提供多账户凭据或任意 GGUF 管理。模型档案不携带密钥，运行按快照 provider/地址检查对应凭证。

运行日志保留最近 200 条；没有多账户管理、播放器或编辑器。真实服务/GPU证据与人工验收边界见[验证状态](status.md)。
