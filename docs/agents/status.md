# 验证状态与后续开发入口

基线日期：2026-10-10。这里区分历史真实联调、当前代码自动化和未完成的发行/人工验收；不能把全部项目笼统标为完成。本文件是后续 Agent 的交接入口，更新时记录实际证据及适用版本，不只修改测试总数。

## 当前实现

已实现单媒体 FFmpeg/Silero/ElevenLabs 识别、三条翻译路径、提示词与上下文、独立缓存、取消/恢复、单条重翻及严格译文 SRT。桌面已改为图标导航和三步工作台，设置/日志独立，结果绑定任务配置快照；日志支持按行多选复制，第二步源语言统一驱动 ASR 与翻译。零时长识别文字按可配置容差合并或独立显示，默认 500 毫秒。源码入口见[架构](architecture.md)，冻结范围见[产品契约](product.md)。

已加入主密码加密凭证、三服务密钥隔离、解锁与流程门禁；不包含多账户或多组云端服务配置档案。凭证层不依赖操作系统账户，整体跨平台发行适配尚未实施。完整产品验收尚未结束，不能用“原 MVP 都完成了”跳过下面的人工项目。

维护者已指定自有代码与文档使用 [MIT 许可证](../../LICENSE)，Copyright (c) 2026 etnAtker；发行包包含项目许可证并保留第三方声明，见 D010。项目许可选择不代表所有原生间接组件的发行义务已经完成核验。

## 本轮各模型独立提示词验证

2026-10-10，按 D015 将本地模板改为按模型分别保存。1.8B 默认恢复原简单单条模板，7B 默认保留前后文模板；提示词页可切换模型，各自编辑、应用预设和恢复默认。旧共用自定义模板只迁移到原选中模型，旧内置模板使用模型默认值，已开始任务的预览/重翻继续使用原快照。依赖、批次与并发保持原行为。

执行 `dotnet test Cuelify.slnx -c Release --no-restore --filter 'FullyQualifiedName~EmbeddedModelPromptTests|FullyQualifiedName~EmbeddedModelWorkflowTests|FullyQualifiedName~TranslationPromptTests|FullyQualifiedName~SwitchingProvidersRetainsIndependentPromptEdits|FullyQualifiedName~Configuration' --logger 'trx;LogFileName=model-prompts-targeted.trx'`，19 项 Core/Infrastructure 与 19 项 Desktop 通过，编译未报告警告或错误。随后执行 `dotnet test Cuelify.slnx -c Release --no-build --no-restore --logger 'trx;LogFileName=model-prompts-full.trx'`，Core/Infrastructure 189 项、Desktop 76 项通过，共 265 项，零失败/跳过。覆盖默认值、配置迁移、独立修改/恢复/重启、云端隔离、原任务快照、无上下文的实际请求，以及浅色 1240×860、深色 900×640 的真实 XAML 模型选择与预设。

使用 Codex 私有目录中已有的 1.8B Q6_K 测试模型，按新的默认模板进行 1 次实际推理：输入提示词 41 token，译文「今天是美好的一天。」，正常 EOS，RTX 5080 Vulkan 卸载 33/33 层，上下文与权重释放通过。证据在忽略目录 `artifacts/model-prompt-validation/`。未重新下载模型，未修改用户正在使用的普通配置/模型目录；7B 默认内容未变，本轮未重复其实际推理。没有新增云端服务调用。

Headless 截图在 `artifacts/ui-model-prompts/`，检查了两个模型及明暗主题；这些界面证据使用测试夹具，不代表原生 GUI/DPI 验收。需注意 Codex Windows 应用包会重定向测试进程的 AppData 写入：之前测试模型实际在 `%LOCALAPPDATA%\Packages\<Codex应用包>\LocalCache\Local\Cuelify\`，早期日志中的普通配置路径是逻辑路径，不代表用户资源管理器中同名目录的实际文件。

## 上一轮内嵌模型下载与上下文验证

2026-10-09，翻译引擎入口改为「内嵌模型推理」，最终预置 Hy-MT2-1.8B Q6_K 与 Hy-MT2-7B Q4_K_M，兼容旧 1.8B Q4_K_M 配置。按用户最终要求仅保留这两个预置模型，其余候选模型的目录、专用推理分支与文档选项已移除。依赖保持 LLamaSharp/Vulkan Windows 0.27.0，未修改包版本或锁文件。

下载时动态发现官方文件、修订、大小和可用 SHA-256；不冻结远端文件身份。模型下载及默认路径与 `settings.json` 同级，下载使用临时文件、校验后替换，支持取消与续传。每个模型保留自己的路径和实际文件哈希；保存新选择不改变已完成任务快照。统一字幕模板使用前文原译文和后文原文，按实际 token 预算裁剪较远参考，保留当前字幕与翻译指令；本地预览使用同一套渲染与预算。

最终执行 `dotnet build Cuelify.slnx -c Release --no-restore`，零警告/零错误；执行 `dotnet test Cuelify.slnx -c Release --no-build --no-restore --logger 'trx;LogFileName=embedded-models-final.trx'`，Core/Infrastructure 188 项、Desktop 69 项通过，共 257 项，零失败/跳过。覆盖动态文件改名与更新、续传及服务器忽略 Range、坏文件保护旧模型、取消保留片段、HTTP 错误、旧配置与自定义模板迁移、模型切换和原任务隔离、下载期间的导航/锁定/关闭、上下文及 token 裁剪、缓存复跑和字幕对齐。HTTP 下载与桌面业务夹具是模拟结果。

两个 Hy-MT2 模型均通过生产 `ModelDownloadService` 从官方仓库实际下载到开发机配置目录并完成当次大小/哈希校验。通过生产 `EmbeddedTranslationEngine` 和 `TranslationOrchestrator` 在 RTX 5080 上分别实际生成 5 次、推理中取消 1 次，再验证 3 条字幕缓存命中（0 次新推理）及单条重翻。两模型均实际 Vulkan 卸载 33/33 层，成功生成正常 EOS，取消后恢复、上下文释放、权重释放及严格 SRT 的 ID/数量/顺序/时码保持通过。测试使用短英语上下文样本，不代表长片或所有语言的语义质量验收。

实际 GPU/下载证据与临时验证入口位于忽略目录 `artifacts/model-validation/`、`artifacts/model-inference/`；最终 headless 截图在 `artifacts/ui-embedded-models/`，已检查浅色 1240×860、深色 900×640。界面测试确认新配置只显示两个预置选项及下载进度，截图使用模拟下载。未新增 ElevenLabs、兼容 API 或 DeepSeek 调用，未进行原生 GUI、DPI、独立发行机器或跨平台验收。

## 提示词变量 UI 验证

将提示词页的纯文本变量列表改为可换行按钮：悬停和键盘聚焦展示中文说明，点击/键盘激活复制完整占位符，附近显示成功或失败反馈；Escape 关闭说明。变量名复用 Core 白名单，云端/本地所需变量在说明中区分，不改变翻译请求或模板校验。按钮内容使用 TextBlock，避免下划线被识别为助记键而消失。

执行 `dotnet build tests/Cuelify.Desktop.Tests/Cuelify.Desktop.Tests.csproj -c Release --no-restore`，零警告/零错误；最终执行 `dotnet test tests/Cuelify.Desktop.Tests/Cuelify.Desktop.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~PromptVariableTests' --logger 'trx;LogFileName=prompt-variable-tests.trx'`，3 项通过。覆盖真实 XAML 数据模板与说明绑定、鼠标悬停、点击复制、键盘说明/Enter/Escape、剪贴板失败与重试、复制不修改模板，以及浅色 1240×860/深色 900×640 的布局边界。

Headless 截图位于忽略目录 `artifacts/ui-prompt-variables/`，剪贴板使用测试替身；没有进行原生剪贴板/GUI/DPI 人工验收，没有真实 API 或模型调用。本轮按 UI 范围验证，未重复无关全量测试。

## 本轮主密码凭证验证

2026-10-09，在 Windows x64 / .NET SDK 10.0.301 上实现跨平台主密码凭证层。新增 Infrastructure `CredentialStore`、桌面凭证状态与主密码弹窗；移除环境变量 Key 读取，未新增依赖或更改锁文件。

执行 `dotnet build tests/Cuelify.Desktop.Tests/Cuelify.Desktop.Tests.csproj -c Release --no-restore`，零警告/零错误；执行 `dotnet test Cuelify.slnx -c Release --no-restore --logger 'trx;LogFileName=master-password-tests.trx'`，Core/Infrastructure 172 项通过。补充弹窗生命周期测试后，最终执行 `dotnet test tests/Cuelify.Desktop.Tests/Cuelify.Desktop.Tests.csproj -c Release --no-restore --logger 'trx;LogFileName=master-password-tests.trx' --blame-hang-timeout 30s`，Desktop 59 项通过，共 231 项，零失败/跳过。

覆盖加密文件不含明文、目录迁移后解锁、错误密码、密文与元数据篡改、版本/参数上限、新 nonce、改密、锁定、取消/占用写入保持旧文件、陈旧窗口覆盖保护、重置保留配置和缓存；桌面覆盖首次使用、门禁与导航、服务密钥及原任务重翻隔离、地址确认、保存后按钮即时更新、锁定后导出、全服务脱敏、实际弹窗成功/错误/取消和明暗主题。

测试夹具曾在 UI 线程同步等待异步保存导致挂起；已将夹具初始化移到后台，并串行执行共享 Avalonia 应用的测试，最终完整桌面运行通过。中断与诊断产物留在 Git 忽略的 TestResults，本轮成功证据以最终 TRX 为准。

通过 `CUELIFY_UI_ARTIFACTS` 将 headless 截图保存到忽略目录 `artifacts/ui-credentials/`，检查浅色 1240×860、深色 900×640、锁定密钥页与三个主密码弹窗，未发现裁切。截图和业务请求使用测试替身，不是原生人工验收。没有调用 ElevenLabs、兼容 API、DeepSeek 或真实 Vulkan 模型；此前真实媒体与 GPU 证据仍列在后面的独立章节。未验证 Linux/macOS、原生 GUI、DPI 或独立发行机器。

主密码限制调整：按用户要求去掉最小/最大长度校验、输入框长度上限及长度提示，不检查字符构成；保留必填与两次输入一致性校验。执行 `dotnet test Cuelify.slnx -c Release --no-restore --filter 'FullyQualifiedName~CredentialStoreTests|FullyQualifiedName~CredentialWorkflowTests' --logger 'trx;LogFileName=password-policy-tests.trx'`，凭证存储 12 项、桌面凭证流程 15 项通过，共 27 项；覆盖单字符符号密码和 2048 字符中文密码的创建、改密与解锁。未重跑无关全量测试，也未重新导出截图。

## 上一轮日志、语言与零时长自动化基线

日志、语言与零时长修复在 Windows x64 原生开发环境执行：

```powershell
dotnet build Cuelify.slnx -c Release --no-restore
dotnet test Cuelify.slnx -c Release --no-restore --logger 'trx;LogFileName=log-language-tests.trx'
```

本轮未改变依赖，未重跑锁定还原；构建零警告/零错误，Core/Infrastructure **161** 项及 Desktop **44** 项测试通过，共 **205** 项，零失败/跳过。计数来自本机本轮 TRX，不是今后固定必须保持的测试数量。测试结果位于各测试项目 Git 忽略的 `TestResults/`，不随仓库提交；新修改必须按影响重新验证。

新增桌面覆盖：媒体检查与步骤准入、设置往返保持选项、处理中切页与锁定、缺少凭据定位设置、任务快照预览/重翻/导出、部分结果与取消、交错进度合并、服务测试不覆盖任务、无效媒体/缺失工具分流，以及浅深色与最小窗口的真实 XAML 渲染。

新增覆盖零时长首尾/连续/重复文本、500 毫秒临界点、孤立显示、交叠说话人排序、改变容差复用识别缓存；日志 Ctrl/Shift 指针多选、Ctrl+C/Ctrl+A、重复文本按行复制与集合移位；语言代码迁移、任务快照及上传前校验。

本轮 headless 截图保存在本机忽略目录 `artifacts/ui-log-language/`，输入和业务结果为测试替身。它们验证浅色 1240×860、深色 900×640 的绑定与布局，不证明真实 ASR、服务、Vulkan、原生剪贴板、拖放或 DPI。

## 先前真实媒体验证（凭证改动之前）

2026-10-09，使用用户指定的 7 分 21.55 秒视频，通过正式 `DesktopJobService.TranscribeAsync`、`HyMt2TranslationEngine` 与 `TranslationOrchestrator` 完成 ElevenLabs Scribe v2 → Hy-MT2 Vulkan → 严格译文 SRT。临时验证入口及证据位于 Git 忽略的 `artifacts/live-validation/`，未新增生产依赖或将凭据写入仓库。

- 故障诊断新增 1 次真实请求，HTTP 200，日语响应中包含零时长词，确认原解析拒绝这些词的原因；原始正文只保存在忽略目录，不进入诊断日志。
- 完整识别：2 个分片、2 次新请求、0 次 ASR 缓存命中、2694 个词（334 个零时长词），组装 158 条字幕，容差为 500 毫秒。
- 完整翻译：158 次实际本地推理、0 次翻译缓存命中，全部成功；生成 1209 tokens，全部正常 EOS。RTX 4070 Laptop GPU 实际 Vulkan 卸载 33/33 层，推理上下文及模型释放通过。
- 导出 158 条中文字幕；翻译前后 ID、数量、顺序、源文及起止时间一致。识别缓存复跑为 0 次新请求、2 次命中；最终代码再次复跑的全部 cue 与真实翻译输入一致。
- 本轮没有调用 OpenAI-compatible 或 DeepSeek，相关真实证据仍属下文历史记录。没有进行原生 GUI 人工操作、听音同步、独立无 SDK 机器或 DPI 验收；本轮真实链路是正式生产组件联调，界面交互证据来自 headless 测试。

## 历史真实验证

以下来自 2026-10-09 原始决策和脱敏运行证据，早于后续 UI 改动。文案整改和分步重构当时没有新增付费服务或 GPU 联调；本轮新证据单列在上节。这些历史结果不能冒充新 UI 版本的端到端人工验收。

| 路径 | 已有真实证据 | 结论范围 |
| --- | --- | --- |
| FFmpeg / Silero CPU | 真实音频准备、语音检测、多分片规划、时码和短媒体流程 | 短样本验证通过；不等于长片听音验收 |
| ElevenLabs Scribe v2 | 三分片识别和缓存复跑，后续实际 Win32 窗口驱动服务的短视频识别 | multipart/词时码及短流程通过；不能由 fake HTTP 代替 |
| OpenAI-compatible | 按用户要求使用 DS 地址/model 的真实字幕请求及缓存复跑 | 仅验证该 DS 兼容端点，未独立验证其他兼容提供商 |
| DeepSeek 官方 | 思考开/关的真实请求、最终 content、ID/时码和缓存复跑 | 短样本及当时 API 映射通过；上游变化需重新核验 |
| Hy-MT2 Vulkan | 官方 GGUF，10 条真实生成、EOS、上下文隔离、取消/恢复和释放；桌面及发布后端再次生成 | 实际 Vulkan DLL、RTX 4070 Laptop GPU、33/33 层卸载；缓存命中不算新 GPU 生成 |
| win-x64 self-contained | 必要资产/许可/哈希、包内运行时、原生窗口启动与正常关闭 | 本机发行检查通过；不能代替独立无 SDK 机器 |

历史本地环境为 .NET SDK 10.0.301、LLamaSharp/Vulkan Windows 0.27.0、ONNX Runtime 1.30.0、RTX 4070 Laptop GPU 与当时驱动。当前依赖版本以项目和锁文件为准，硬件/驱动是测试机器信息，不是产品唯一支持的设备。

历史 Win32 窗口流程由工具驱动真实 ViewModel 和服务，文件对话框为替身，并非人工点击验收。原始 Spike 已移出解决方案，旧运行命令不能在当前仓库直接执行；后续应使用正式窗口、生产引擎和可维护测试。

## 历史证据位置

清理时已创建开发机仓库外归档 `Cuelify-archives/pre-initial-commit-20261009-133845/`，不随仓库分发，不要求其他开发者拥有该路径：

- `repository-before-cleanup.zip` 保存原 `docs/MVP_SPEC.md`、`docs/CODEX_EXECUTION.md`、完整旧 `docs/decisions.md` 等源码/文档快照。
- `removed/docs/validation/phase0/` 保存模型、GPU、Silero 与初次 ASR 证据；`phase1/` 保存真实分片识别、缓存复跑及原文 SRT。
- `removed/docs/validation/phase2/` 保存兼容接口、DS 思考开/关、本地 Vulkan 及缓存/模拟的分别报告。
- `removed/docs/validation/phase3/` 保存原生窗口、发布后端、启动/关闭和发行一致性证据。
- 清理摘要和旧发行包另保留在该归档中。普通开发只需本体系；没有旧归档时不得声称独立核验过原始日志。

文档中的证据目录是历史目录说明，不是有效的当前仓库链接。新证据放 `artifacts/` 或仓库外，记录日期、配置、环境、真实请求/缓存计数及脱敏结论，不提交日志和私密媒体。

## 已知限制与待验收

| 项目 | 当前状态与最小检查 |
| --- | --- |
| 主密码原生与跨平台验收 | 待做：原生弹窗、键盘、系统关闭与文件权限；Linux/macOS 构建发行适配及凭证文件互通尚未验证 |
| 新三步 UI 的原生人工验收 | 待做：文件对话框、单文件拖放、键盘 Tab/焦点、切页、步骤、取消/重试/导出及关闭 |
| DPI 与长中文 | 待做：Windows 100%/150%/200%，最小窗口与长路径/长字幕，不仅检查 headless 截图 |
| 独立无 SDK 机器 | 待做：完整 self-contained 目录启动，确认自带运行时、native 资产和资源正常 |
| 长媒体与播放器 | 待做：长停顿/连续语音边界听音、跨小时字幕、常见播放器读取及原媒体同步 |
| 翻译语义质量 | 短样本中已有称呼/语境偏差，例如 sonny 的译法；结构对齐和 EOS 不代表语义满分 |
| 新 UI 版本真实回归 | 本轮指定视频的 Scribe v2/Hy-MT2 生产链路通过；原生 GUI 人工、其他云端路径仍待按授权与风险验证 |
| 第三方发行许可核验 | 许可全文已保留；待按实际 Windows 原生构建核对间接组件、适用许可及对应源码获取说明，不能用主库 MIT 或资产检查代替 |

没有已知的当前代码构建阻塞。上述待验收项限制的是交付结论，不应通过扩大技术栈、增加播放器或伪造测试来解决。

## 后续 Agent 的工作起点

先确认用户的新任务及批准范围，按[索引](README.md)阅读相关文档和源码。若目标是正式发行验收，优先完成新 UI 原生交互、DPI、独立机器与长媒体检查；若目标是功能修复，围绕该问题修改并更新相关契约/测试。不要重新创建 Phase 0～3 工程，不自动下载其他模型或重复云端请求。

新的报告应明确“本轮验证”“沿用历史证据”“未验证”，必要时更新本文件及对应决策。测试数量、日期和历史 GPU 层数都不能作为无需复验的永久保证。
