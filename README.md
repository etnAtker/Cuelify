# Cuelify

Cuelify 是 Windows x64 字幕桌面工具：选择一个本地视频或音频，经语音识别和翻译后导出 UTF-8 SRT。

## 功能

- FFmpeg 音频处理与 Silero VAD 静音优先分片，保留原媒体时间轴。
- ElevenLabs Scribe 词级语音识别，成功分片自动缓存。
- OpenAI-compatible、DeepSeek 官方 API、进程内 Hy-MT2 Vulkan 三种翻译引擎。
- 可编辑、保存的提示词和预设，以及不含认证头的请求正文预览。
- 原译文对照、阶段进度、取消、缓存恢复、单条重翻和译文 SRT 导出。
- 明暗主题；API Key 仅保留在当前会话。

## 运行要求

- Windows 11 x64。
- Windows x64 `ffmpeg.exe` 和 `ffprobe.exe`：加入 PATH，或在界面的「识别与音频高级参数」填写绝对路径。
- ElevenLabs Speech-to-Text API Key；云端翻译另需对应服务的 Key。
- 本地翻译需要支持 Vulkan 的 GPU/驱动和指定 GGUF 模型，云端翻译无需本地模型。

发行采用 .NET 10 自包含目录，无需安装开发 SDK。解压整个发行目录后运行 `Cuelify.Desktop.exe`，保留附带的原生 DLL 和资源。FFmpeg 和 GGUF 不随包提供；Silero ONNX 随包提供。

## 使用

1. 在左侧「设置」配置 ElevenLabs Key 和翻译服务。云端填写地址、模型和翻译 Key；DeepSeek 默认配置为 `https://api.deepseek.com` / `deepseek-flash`。本地选择下述指定 GGUF。按需编辑提示词和高级参数，然后保存设置；连接测试会真实请求服务或执行本地生成。
2. 返回「工作台」，在「选择文件」步骤选择或拖入一个本地媒体，点击「下一步」检查文件信息。
3. 在「翻译选项」设置本次任务的语言与引擎，点击「开始处理」。服务设置与提示词可从此步骤直接进入，返回时保留任务选项。
4. 在「生成字幕」查看进度和原译文。处理中可以切换到设置或日志页面，任务会继续运行；相关参数暂时不可修改。取消或部分失败后可继续处理，复用成功结果。
5. 全部译文完成后导出 SRT，选中字幕可定向重翻。已完成的任务使用启动时的配置；后续修改设置不会影响其导出或单条重翻。调整任务选项后重新处理，可生成另一种语言或风格的结果。

识别会上传音频，云端翻译会发送原文及上下文，可能产生费用；失败重试和定向重翻也可能再次计费。相同输入与识别参数复用 ASR 缓存，更换翻译配置不会清空已完成的识别结果。部分失败时保留成功译文，完整 SRT 导出要求所有条目有效。

### 本地模型

仅支持腾讯官方 [Hy-MT2-1.8B-Q4_K_M.gguf](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF/blob/main/Hy-MT2-1.8B-Q4_K_M.gguf)。手动下载并在界面选择，程序校验文件名和 SHA-256：

```text
dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699
```

模型通过 LLamaSharp 在程序进程内运行，使用 Vulkan GPU 卸载；不启动外部模型服务。点击「测试本地翻译」可验证模型运行，GPU 卸载详情见运行日志。模型、驱动或 GPU 不可用时明确报错，不回退 CPU-only。

## 数据与凭据

非敏感配置和提示词存放在 `%LOCALAPPDATA%\Cuelify\settings.json`，识别和翻译缓存分别位于 `Jobs`、`Translations`。缓存含音频、原文和译文，分享前请检查内容。

API Key 在密码框中输入，也可通过启动进程环境变量 `ELEVENLABS_API_KEY`、`TRANSLATION_API_KEY` 或 `DEEPSEEK_API_KEY` 提供。Key 不写入普通配置或日志；关闭时清除窗口持有的凭据。启动 shell 的环境变量由调用者清理。

## 从源码运行

安装 `global.json` 指定的 .NET 10 SDK，并准备 FFmpeg/ffprobe。在仓库根目录执行：

```powershell
dotnet restore Cuelify.slnx --locked-mode
dotnet run --project src/Cuelify.Desktop -c Release --no-restore
```

完整文档入口见 [文档索引](docs/README.md)。构建、测试、发布及资源来源见 [开发指南](docs/development.md)，运行问题见 [故障排查](docs/troubleshooting.md)。源码仓库中的后续 Agent 从 [AGENTS.md](AGENTS.md) 和 [Agent 文档索引](docs/agents/README.md) 开始。

## 许可证

Cuelify 自有代码与文档采用 [MIT 许可证](LICENSE)，Copyright (c) 2026 etnAtker。

第三方依赖与资源保留各自许可证，版权和声明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 与 `licenses/`。GGUF 和 FFmpeg 不随包提供，按其来源许可使用；项目的 MIT 许可证不替代第三方许可或云端服务条款。
