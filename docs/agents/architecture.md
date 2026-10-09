# 当前架构与数据流

本文件描述现有生产代码，而非最初 MVP 的建议接口。修改前仍需阅读对应源码和测试。范围见[产品契约](product.md)，环境与命令见[开发指南](../development.md)。

## 分层与依赖

```text
Cuelify.Desktop → Cuelify.Infrastructure → Cuelify.Core
                      ↓
          FFmpeg / ONNX / HTTP / LLamaSharp
```

Core 定义领域模型、接口和纯逻辑，不引用 Avalonia 或 Infrastructure。Infrastructure 实现 I/O、原生引擎和业务编排，不拥有窗口状态。Desktop 负责配置编辑、会话凭据、任务命令、页面及原生文件对话框，不另造识别或翻译流水线。

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
| `Infrastructure/Translation/Local` | 固定模型身份、Vulkan 运行时、GPU 证据、采样与推理 |
| `Infrastructure/Storage` | `AtomicFile`：哈希、原子 JSON/文本读写 |
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

## 翻译流程

```text
原文 cue → 批次/上下文 → PromptBuilder → ITranslationEngine
        → AlignmentValidator → 部分接受/有限逐条补翻
        → 原子缓存 → 已校验结果进度 → 完整性判定 → SRT
```

- 兼容 API 与 DeepSeek 官方适配器复用 `ChatCompletionsTransport`，但请求能力映射分别处理。普通请求为非流式，只接收最终 `content`；思考文本不进入字幕。
- `PromptBuilder` 使用有限变量白名单，一次替换并正确处理 JSON；不执行模板表达式，也不再解释原文中的大括号。预设和输出格式位于 Core 的生产类型，不另写一套 UI 专用规则。
- 云端批次受条数与字符预算限制。固定波次并发，前文取该波次开始时的稳定快照，避免完成顺序改变上下文和缓存身份。
- 本地强制批次 1、并发 1、纯译文输出。`HyMt2TranslationEngine` 复用已加载权重，使用 GGUF chat template 和独立上下文，核查实际 tokenizer 预算、EOS、超时与取消。
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
| `TaskSourceCode/Language`、`TaskTargetLanguage`、`TaskProviderIndex` | 工作台本次任务选项，默认值来自已保存设置 |
| `_jobSettings`、`_jobInputPath` | 实际任务启动时的配置和文件，结果预览、重翻及导出依据 |
| `Rows` / `SelectedCue` | 字幕和选择；区分等待、翻译中、失败、取消，原文和时间保持稳定 |
| `IsBusy` / `_isMediaJob` / cancellation | 串行保护长操作，区分媒体处理与服务测试 |
| `ElevenLabsKey` / `TranslationKey` | 仅窗口会话与启动进程环境，非 AppSettings 成员 |

任务启动从已保存配置复制，再覆盖本次选项。保存设置只更新默认配置，未保存的修改不进入媒体处理请求。服务测试是设置页的显式动作，校验并使用当前设置草稿；它不改变已完成任务状态或导出资格。

单条重翻和请求预览使用原任务配置；导出命名使用原任务目标语言。更换输入清除旧任务和结果；新建任务回到第一步并载入已保存默认值。调整选项后重新处理才生成另一份配置对应的结果。配置正确且初始化完成后，命令按状态启用；处理中允许导航，相关编辑和步骤切换锁定。

## 缓存与敏感信息

用户数据根为 `%LOCALAPPDATA%\Cuelify`：

| 路径 | 内容 |
| --- | --- |
| `settings.json` | 非敏感设置、三个引擎各自的提示词 |
| `Jobs/<job-id>/` | 输入/音频/VAD/分片/识别记录及状态 |
| `Translations/<identity>/` | 翻译批次、结果及状态 |
| `Working/` | 处理期间的临时输出 |

识别与翻译缓存身份分开；翻译身份包含引擎非敏感签名、模板、语言、风格和原始 cue，批次请求还包含实际上下文。API Key 不进入身份。配置拒绝高级 JSON 中受控或嵌套凭据字段，以及误贴的当前会话 Key。

原子写保护完整性，作业文件句柄提供互斥；锁文件仍在不表示作业仍被占用。缓存包含音频、原文、译文，不能当作可公开测试 fixture。迁移缓存格式要考虑身份变化、旧数据校验和重新计费影响。

## 线程、取消与释放

桌面服务将 CPU/原生重操作放到后台，进度经 Dispatcher 更新 UI。HTTP、Process 和推理共享取消传播；旧操作完成后的延迟通知不能污染新任务。重入由命令与业务状态共同保护。

关闭窗口先请求取消，等待当前操作，再释放本地权重、HTTP 和会话凭据，最后排队关闭窗口，避免同步释放时重入 Closing。配置改变使本地引擎身份变化时，先释放旧权重再创建新实例。窗口事件订阅需有对应取消订阅，不能因导航增加多份回调。

## 当前实现的明确限制

云端地址、模型及生成参数目前由兼容接口与 DeepSeek 共用一组配置字段；提示词按三个引擎分别保存，云端会话 Key 目前也共用一个输入。工作台可选 provider，但不代表已实现多个服务配置档案或多账户凭据管理。以后扩展这些能力需要独立计划、兼容旧 JSON 并测试配置隔离，不能在交接中声称已经具备。

运行日志保留最近 200 条；没有持久化密码记忆、播放器或编辑器。真实服务/GPU证据与人工验收边界见[验证状态](status.md)。
