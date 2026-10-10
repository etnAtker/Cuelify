# 验证状态与后续开发入口

基线日期：2026-10-10。这里区分历史真实联调、当前代码自动化和未完成的发行/人工验收；不能把全部项目笼统标为完成。本文件是后续 Agent 的交接入口，更新时记录实际证据及适用版本，不只修改测试总数。

## 当前实现

已实现单媒体 FFmpeg/Silero/ElevenLabs 识别、三条翻译路径、提示词与上下文、独立缓存、取消/恢复、单条/多选重翻及严格译文 SRT。桌面已改为图标导航和三步工作台，设置/日志独立，结果绑定任务配置快照；日志支持按行多选复制，字幕支持 Ctrl/Shift 多选重翻及失败标红，第二步源语言统一驱动 ASR 与翻译。零时长识别文字按可配置容差合并或独立显示，默认 500 毫秒。源码入口见[架构](architecture.md)，冻结范围见[产品契约](product.md)。

已加入主密码加密凭证、三服务密钥隔离、解锁与流程门禁；不包含多账户或多组云端服务配置档案。凭证层不依赖操作系统账户，整体跨平台发行适配尚未实施。完整产品验收尚未结束，不能用“原 MVP 都完成了”跳过下面的人工项目。

维护者已指定自有代码与文档使用 [MIT 许可证](../../LICENSE)，Copyright (c) 2026 etnAtker；发行包包含项目许可证并保留第三方声明，见 D010。项目许可选择不代表所有原生间接组件的发行义务已经完成核验。

## 本轮译文校验与多选重翻验证

2026-10-10，按批准的 D019 删除原译文相同时判定失败的规则及语言豁免参数；服务响应、缓存读取和定向重翻恢复旧结果统一保留非空、非法控制字符、输出协议与 ID 对齐校验，缓存身份和 schema 不变。只接受实际服务返回或已有成功缓存，不用原文填补失败结果。旧失败条目需重新请求，未修改用户任务缓存、设置或凭证。

字幕列表复用 SelectionModel 支持 Ctrl 多选、Shift 连选，显示已选数量；单条/多条重翻捕获所选 ID 集合、使用原任务快照，确认替换及费用，仅清除所选结果。行更新按 ID 恢复选择，新任务清空选择；TargetedRetry 显示重新翻译中并更新行状态，取消/失败不恢复旧成功值，失败译文使用浅深色主题错误色及文字，成功后恢复正常样式。

执行 `dotnet test tests/Cuelify.Tests/Cuelify.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~TranslationPromptTests|FullyQualifiedName~TranslationOrchestratorTests' --logger 'trx;LogFileName=translation-validation-multiselect-final.trx'`，51 项通过；执行 Desktop Release 编译测试后，以 `dotnet test tests/Cuelify.Desktop.Tests/Cuelify.Desktop.Tests.csproj -c Release --no-build --no-restore --logger 'trx;LogFileName=subtitle-multiselect-desktop-final.trx'` 完成最终全量桌面复验，121 项通过、1 项真实 GPU 门控跳过，零失败。TRX 位于各测试项目忽略的 TestResults/。覆盖相同文本的纯文本/JSON 接收及缓存复用、无效输出有限重试、多个指定 ID 的请求范围和失效缓存、未选中结果/时间轴保留、原配置快照、选择保持/重置，以及多选重翻取消/失败、真实 headless Ctrl/Shift 鼠标输入和浅深色 XAML 失败色/恢复。初次鼠标用例点击超出可见区域，改为滚动目标行可见后复验通过；测试刷色断言使用 ISolidColorBrush 兼容不可变主题画刷。

本轮未新增 ElevenLabs、兼容 API、DeepSeek 付费请求，也未运行 Hy-MT2 实际 GPU 生成；headless 与业务替身不能作为真实服务、发行或原生 GUI/DPI 人工验收。详细拒收原因日志、识别分片完成进度与按阶段继续处理不在本轮已实施范围，仍待独立计划。

## 上一轮移除模型校验与服务复用验证

2026-10-10，按批准的 D018 移除手动选择和运行前的模型 SHA、GGUF 文件头及架构/量化校验。手动选择不再联网查询官方文件身份，任意可读文件均可记录；预设 ID 仍用于设置默认值与提示词关联，不证明文件身份。下载完整性校验保持。模型缓存版本仅含路径、大小和修改时间，历史配置哈希不参与运行判断；旧缓存保留，但新本地缓存协议与旧身份隔离。

生产 `DesktopJobService` 先比较运行配置和文件版本，再查询已有服务的轻量健康接口；同配置、同版本且可用时直接复用，不重复运行包能力检查或模型加载。准备状态不再包含模型校验，复用时明确显示正在复用本地服务；加载失败保留 llama.cpp 原始诊断，经既有脱敏后写入日志。取消、超时、文件句柄及子进程释放保持。

Windows 原生 Release 普通自动化：Core/Infrastructure 221 项通过、4 项 GPU 门控跳过，Desktop 115 项通过、1 项真实服务门控跳过，均零失败。覆盖文件版本隔离与缓存复用、不读取占用中的模型字节、离线手动选择非 GGUF 文件、无效历史 SHA 不作门禁、加载失败提示及诊断脱敏。TRX 位于忽略目录 `artifacts/model-loading/tests/`。

另显式执行 3 项真实 Vulkan 用例，全部通过：正常模型冷启动/复用并实际生成；损坏模型启动 llama.cpp 后收到真实加载错误并释放进程和文件；正式桌面服务工厂跨准备、测试翻译和请求预览复用同一已加载服务，修改历史 SHA 也不重新加载。使用已有 1.8B Q6_K 与 b11540 官方 Vulkan 运行包，RTX 4070 Laptop GPU 实际卸载 33/33 层，GPU 模型缓冲 1401.61 MiB。共 2 次新短句生成、零缓存命中；正常生成测试检查 EOS。证据在 `artifacts/model-loading/gpu/preparation-evidence.json`、`desktop-service-evidence.json`、`damaged-model-server.log` 及对应 TRX。没有重新下载模型/运行包、付费线上或 ASR 调用、改写用户现用设置/凭证，也未进行 7B、发行或原生 GUI/DPI 人工验收。

损坏模型的诊断收集边界调整后，定向真实用例再次通过，记录为 `damaged-model-final.trx`；不产生新的成功生成。最终 `git diff --check` 通过。

## 上一轮准备反馈与推理参数统一验证

以下是上一轮历史证据，其中模型完整性校验和复用显示已就绪的描述已由本轮 D018 替代。

2026-10-10，工作台检查媒体与开始处理前的准备，以及设置页翻译测试，已在固定操作区显示真实阶段、等待指示和已用时间；日志记录开始、完成、取消/失败及耗时。llama-server 冷启动上报运行包/模型检查、进程启动、模型加载、容量/GPU 检查与就绪，运行中的同一会话只上报就绪。移除工作台额外的整文件 SHA 校验，服务工厂与启动会话仍执行完整性检查。阶段通知通过 UI dispatcher 排空后移交识别或最终测试结果，取消/关闭停止计时，陈旧通知不覆盖后续状态。

翻译服务页统一推理参数的位置、中文名称、token/秒单位和数值输入，保留本地/线上各自的并发及输出值、原默认值和 schema 2。线上输出可留空；本地输出上限随模型上下文半容量变化，缩小容量同步降低超出的输出值。参考上下文与本地模型上下文名称区分，专属参数保持按引擎显示。

本轮 Windows 原生 Release 桌面测试 113 项通过（新增 9 项），覆盖两类参数切换/保存、清空线上输出、上下文联动、准备显示与取消/失败后重试/关闭、阶段通知和最终状态保持。核心/基础设施普通测试 223 项通过、3 项 GPU 门控跳过；其中新增真实准备验证已单独显式执行并通过，使用现有官方 1.8B Q6_K 与 b11540 Vulkan 运行包，冷启动阶段顺序正确，第二次准备复用同一 PID，一次新短句生成成功、5 token、EOS、零缓存、33/33 层 GPU 卸载并释放进程。未新增付费线上请求或 ASR 调用，未重新进行 7B、发行或原生 GUI/DPI 人工验收。

TRX 与真实 GPU 证据在 `artifacts/inference-ui/tests/`、`artifacts/inference-ui/gpu/`；headless 浅色 1240×860、深色 900×640 截图在 `artifacts/inference-ui/screenshots/`，已检查固定反馈区、按钮与参数区布局。截图及桌面业务替身不代表原生 GUI 或真实媒体端到端验收。

## 变量说明焦点修复验证

2026-10-10，用户反馈点击变量说明后，第一个变量的解释立刻出现在屏幕左上角，悬停正常。真实 headless XAML 在浅色 1240×860、深色 900×640 都复现浮层打开即开启第一个变量的 ToolTip，修复前 TRX 为 variable-focus-before-fix.trx，2 项失败。核对 Avalonia 11.3.12 的 PopupFlyoutBase 和 InputElement 源码：标准浮层会主动调用 Focus()，其默认导航来源是 Unspecified；原应用把所有非 Pointer 来源当作键盘导航而立即开启说明。ToolTip 默认 Pointer 定位与截图现象相符，但屏幕左上角的原生坐标未在 headless 中复验。

修复为仅 Tab/Directional 聚焦主动显示说明，忽略程序自动聚焦；Button.prompt-variable 的共享样式将说明锚定按钮下方，并把悬停 ShowDelay 从框架默认 400 毫秒缩短为 200 毫秒，其他控件不改。执行 `dotnet test tests/Cuelify.Desktop.Tests/Cuelify.Desktop.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~PromptVariableTests --logger 'trx;LogFileName=variable-focus-final.trx'`，3 项通过，零失败/跳过，编译零警告/零错误。覆盖真实鼠标点击打开浮层后全部说明关闭、程序聚焦不弹说明、实际悬停计时、按钮锚点、Tab/方向键、鼠标与 Enter 复制、Escape、关闭后再打开及复制失败重试；截图位于忽略目录 artifacts/variable-focus-fix/。

本轮仅修改焦点处理和变量样式，采用定向回归，没有重复上一轮桌面全量测试、真实 GPU 或付费服务验证。用户现用 Rider 实例、配置与凭证未改；Windows 原生弹窗位置及人工体验仍需重新构建后复验。最终 `git diff --check` 通过。

## 上一轮提示词页面与切换确认验证

2026-10-10，按用户批准重排提示词页：左侧保留搜索/分类/列表，右侧固定工具栏及正文、模板设置、提示词预览三个页签，底部只保留一个保存设置入口。变量说明改为就近浮层，正文不被说明挤占；进入预览页签使用当前草稿渲染样例，分开显示系统与用户消息并支持复制，不调用推理服务。模型使用情况移入模板设置的辅助区，区分默认使用与显式指定，不作为独立“模型关联”页签。

未保存切换先拒绝并恢复原列表高亮，弹模态框询问保存并切换、放弃并切换或取消；新建/复制共用这套保护。保存使用配置快照，磁盘写入成功后才更新模板库和清除编辑状态；校验或写入失败保持草稿和原选择。未保存自定义模板可确认删除，仅移除草稿；删除已保存模板时提示被引用模型将恢复各自默认提示词，确认后原子移除模板及相关显式关联。本轮用户明确调整了上一轮手选替代模板的规则，线上、1.8B、7B 各自默认值保持。

执行 `dotnet test tests/Cuelify.Desktop.Tests/Cuelify.Desktop.Tests.csproj -c Release --no-restore --logger 'trx;LogFileName=prompt-ui-desktop-final.trx'`，104 项通过，零失败/跳过，编译零警告/零错误。覆盖真实 XAML 模态框三种结果及所属窗口、等待弹框时原选择高亮、取消/保存/放弃、校验和磁盘写入失败保留草稿、新建/复制的切换保护、未保存模板删除不写配置、已引用删除按模型恢复默认、预览草稿与错误/复制状态、变量鼠标/键盘说明与复制、已有任务快照。测试组件为 headless 或服务替身；窗口模态的原生输入拦截仍需人工验收。

检查浅色 1240×860、深色 900×640 的正文、设置、预览截图，位于忽略目录 artifacts/prompt-ui-redesign/；正文区域没有遮挡固定保存按钮，列表与操作正常。初轮真实 XAML 暴露了取消后高亮未恢复，以及变量说明折叠后旧测试没有展开的问题，已修正并由最终全量回归覆盖。没有启动或修改用户现用 Rider 实例、配置或凭证，没有新增付费 API、GPU 推理、模型/运行包下载或发行验收；推理路径未改变，本轮没有重复上一轮 GPU 验收。最终静态检查以本轮 `git diff --check` 为准。

## 上一轮独立提示词库与批量推理验证

2026-10-10，按用户批准的 D017 分离提示词管理与模型设置，内置仅保留「通用字幕批量翻译」「简单单条翻译」「上下文单条翻译」，去掉简洁字幕批量模板。内置只读，自定义支持新建、复制、搜索、编辑和删除；模型仅关联模板 ID，多个模型可共用模板，删除已引用模板必须指定替代。编辑器切换保留未保存草稿并提供保存、放弃或取消；模型切换不切换编辑草稿。样例预览只渲染正文，不调用推理服务。

批量属性、每批条数和字符上限归模板管理，兼容 API、DeepSeek、本地 llama-server 均可执行单条纯文本或批量 ID JSON。补翻继续使用原模板和输出协议，本地超出分词预算时先裁剪参考，再拆小批次；单条仍超限则明确失败。缓存身份排除模板名称、说明及 ID，正文或执行模式改变才影响翻译结果缓存。已完成任务保留模板快照。线上默认并发仍为 4，本地为 2。

settings.json 使用 schema 2，按用户要求不迁移旧配置：首次读取旧版设置先保存同目录 settings.previous-*.json，再原子写入新版默认设置，需重新配置模型与服务；凭证、GGUF、运行包和缓存文件保留。损坏或未来版本配置不覆盖。旧 1.8B Q4_K_M 兼容选项移除，列表只含两个当前预设。下文 D015、D016 阶段的配置迁移与旧选项测试仅为历史证据，已由 D017 的新契约替代。本轮没有加载或改写用户现用设置和凭证，配置重置仅在测试夹具中验证，实际行为发生在下一次新版启动。

最终执行 `dotnet test tests/Cuelify.Tests/Cuelify.Tests.csproj -c Release --no-restore --logger 'trx;LogFileName=prompt-refactor-core-final.trx'`，Core/Infrastructure 223 项通过、2 项真实 GPU 门禁用例跳过、零失败；执行 `dotnet test tests/Cuelify.Desktop.Tests/Cuelify.Desktop.Tests.csproj -c Release --no-restore --logger 'trx;LogFileName=prompt-refactor-desktop-final.trx'`，Desktop 94 项通过，零失败/跳过。共 317 项普通自动化通过，测试编译零警告/零错误。覆盖三个生产后端与两种请求模式、部分成功仅补失败 ID、容量拆批、缓存身份、库与关联持久化、旧版备份重置、损坏/未来版本保护、草稿切换、删除替代及已完成任务快照。线上传输与桌面业务使用测试替身，本轮没有新增付费服务调用。

另执行 `FullyQualifiedName~IndependentTemplatesRunSingleContextAndBatchOnRealVulkan` 定向真实 GPU 验收，1 项通过，TRX 为 prompt-refactor-gpu.trx。使用已有完整官方 Vulkan 运行包，版本 `0.6.0-dev (build 11540, commit 1e6f04a75)`，只读已有 Hy-MT2-1.8B Q6_K（SHA-256 `d98fe604dec1f28f58f80d7d560f7177e584d3b8e5835862687660e5ff97cb40`）。三个内置模板共 5 次实际生成、0 次缓存命中，其中批量模板一次请求完成两条字幕且未补翻；均正常 EOS，保留 cue ID 和时码。RTX 4070 Laptop GPU 实际 Vulkan 卸载 33/33 层，模型 GPU 缓冲 1401.61 MiB，关闭后服务进程已退出。证据在忽略目录 artifacts/prompt-refactor/gpu/template-evidence.json、template-server.log；运行命令和环境变量见[开发指南](../development.md)。7B 未在本轮实际生成验收。

真实 headless XAML 覆盖浅色 1240×860、深色 900×640 的列表选择、筛选清除恢复选中、只读模板、自定义编辑与复制、草稿切换和模型列表稳定性，截图位于忽略目录 artifacts/prompt-refactor/ui/，已检查布局。没有进行 Rider 原生人工交互、DPI、原生剪贴板或本轮发行验收。最终 `git diff --check` 通过。

## 上一轮独立 llama.cpp 与并发验证

2026-10-10，按用户批准的 D016 将进程内 LLamaSharp 替换为应用按需管理的 llama-server 子进程，界面统一为「本地模型」。增加官方 Windows x64 Vulkan 运行包下载、取消/续传、SHA-256 校验、安全解压及手动程序检查；版本安装到设置目录的 `llama.cpp/<tag>-<digest-prefix>/`，旧版本保留。模型预设、下载、独立提示词和已完成任务快照保持；新后端缓存身份独立，识别缓存不受影响。本地默认并发 2，云端默认并发 4，范围均为 1～8，旧配置的显式云端值保留。

执行 `dotnet restore Cuelify.slnx --locked-mode` 通过。最终全量 `dotnet test Cuelify.slnx -c Release --no-restore --logger 'trx;LogFileName=llama-final.trx'` 中 Core/Infrastructure 211 项、Desktop 83 项通过；真实 GPU 用例完成生成、取消及释放后因旧 `server.log` 不允许覆盖而失败。修正仅该验收产物覆盖设置，执行同配置的 `FullyQualifiedName~LocalGpuAcceptanceTests` 定向复验，1 项通过；合计 295 项全部覆盖通过，无未修复失败。全量与复验 TRX 分别为 `llama-final.trx`、`llama-gpu-final.trx`，位于各测试项目忽略的 `TestResults/`。

自动化覆盖运行程序接口与 Vulkan 检查、GGUF 架构/量化及模型句柄释放、并发 slot、超时/取消/关闭、最终模板与分词预算、SSE 完整生成校验、稳定波次上下文、缓存身份和字幕对齐；下载覆盖滚动版本选择、官方许可、续传、坏哈希、越界路径及旧版本保护。桌面覆盖默认值和旧设置迁移、手动选择、下载草稿与任务快照、占用/导航/取消/关闭、下载失败及明暗真实 XAML。下载 HTTP 与桌面业务用例使用测试替身，不能代替实际 GPU 或人工 GUI 验收。

通过生产 `LlamaPackageService` 实际下载本次官方滚动 Vulkan 包，最终复验版本 `0.6.0-dev (build 11540, commit 1e6f04a75)`。使用已有 Hy-MT2-1.8B Q6_K（SHA-256 `d98fe604dec1f28f58f80d7d560f7177e584d3b8e5835862687660e5ff97cb40`），经新生产引擎与编排器双并发翻译 4 条字幕，0 次缓存命中，均非零生成并正常 EOS。RTX 4070 Laptop GPU 实际 Vulkan 卸载 33/33 层，模型 GPU 缓冲 1401.61 MiB；实际模板预览 42 token。另在长请求生成至少 2 token 时取消，同一进程随后成功生成，关闭后子进程不存在。证据位于忽略目录 `artifacts/local-acceptance/evidence.json`、`server.log`。本次仅使用测试目录安装运行包和只读已有模型，没有保存或覆盖用户现用设置/凭证；7B 和旧 1.8B Q4_K_M 未在新后端实际生成验收，旧后端历史证据不等于新后端验证。

顺序执行自包含 Windows x64 `dotnet publish` 及 `scripts/Verify-Release.ps1`，发行资产、包内 .NET 运行时、原生主窗口创建和正常退出检查通过，证据在 `artifacts/validation/llama-release.json`；发行包不携带 LLamaSharp、llama.cpp 或 GGUF。首次发行构建与测试并发写入同一构建目录导致 PDB 占用，测试结束后顺序重跑已解决。运行包对应 tag 的 llama.cpp LICENSE 随下载保留，发行锁文件与第三方声明已更新。Headless 浅色 1240×860、深色 900×640 截图在 `artifacts/ui/llama-runtime-*.png`，已检查布局；尚未完成独立无 SDK 机器、原生交互/DPI、父进程异常终止的人工验收。没有新增 ElevenLabs、兼容 API 或 DeepSeek 真实调用，也没有开展跨平台发行验收。

### 本地翻译测试的模板校验修复

2026-10-10，用户在 Rider 运行时反馈默认本地模板包含 `{source_text}`，测试仍报告缺少该变量。默认模板、首次加载、真实 XAML 引擎/模型及页签切换的新增用例未复现默认内容丢失；随后用「当前本地默认有效、未选本地或兼容服务模板无效」的夹具，稳定复现相同错误。原因是测试入口复用了全配置的模板校验；未选模板的错误没有标明归属。

测试入口改为仅校验当前引擎/模型的提示词，当前模板缺少变量仍在调用服务前拒绝；保存与加载配置继续完整校验，错误标明具体引擎或模型。没有自动改写自定义模板或用户配置。执行 Desktop 全量 `dotnet test tests/Cuelify.Desktop.Tests/Cuelify.Desktop.Tests.csproj -c Release --no-restore --logger 'trx;LogFileName=local-prompt-desktop-final.trx'`，94 项通过，零失败/跳过；覆盖未选模板隔离、当前非法模板拒绝、云端反向隔离、默认 XAML 切换和原任务快照。此前故障复现 TRX 为 `inactive-prompt-repro.trx`，其中 2 项在修复前失败，修复后已覆盖通过。服务调用使用测试替身，本轮没有新增付费云端或实际 GPU 调用；用户当前运行实例中的具体异常模板尚待 Rider 重新构建后确认，不能把夹具复现当作读取其实际运行内存的证据。

### 旧 1.8B Q4_K_M 下拉选择修复

2026-10-10，用户反馈点击旧 Q4_K_M 选项后崩溃，Rider 控制台堆栈重复过长。真实 XAML 旧配置夹具确认：刷新模型列表会重新生成数组并重置下拉选择，一次用户选择触发重复配置写入，产生选择回退；初始化时也可能显示 Q6_K 而配置仍为旧 Q4_K_M。修复为共用稳定 `ObservableCollection`、按需增删旧配置选项，切换期间拒绝重入回写。保留旧模型路径、哈希及自定义模板，没有删除模型、迁移用户现用文件或更改推理服务。

服务页与提示词页分别覆盖旧模型初始选中、Q6_K 初始选中但保留旧模型配置两种情况，检查首次显示、四次往返切换仅产生四次配置变化，以及保存/重载后的路径、哈希和模板。修复前夹具失败记录为 `legacy-selection-before-fix.trx`，最终执行 `dotnet test tests/Cuelify.Desktop.Tests/Cuelify.Desktop.Tests.csproj -c Release --no-restore --logger 'trx;LogFileName=legacy-selection-desktop-final.trx'`，Desktop 98 项通过，零失败/跳过。测试使用 headless XAML 和配置夹具，不代表已取得用户原生崩溃完整堆栈或完成 Rider 人工复验。本轮没有新增实际模型推理、模型下载或云端调用。

## 上一轮各模型独立提示词验证

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
