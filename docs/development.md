# 开发指南

本文件负责环境、依赖、命令、发布与资源来源。完整入口见[文档索引](README.md)；源码仓库中的产品契约、架构和 Agent 工作方式见 [Agent 专用索引](agents/README.md)。

## 环境与结构

使用 Windows x64 和 `global.json` 指定的 .NET SDK 10.0.301，可在 Rider 开发。测试中的媒体集成用例要求 `ffmpeg.exe`、`ffprobe.exe` 在 PATH，并使用 Windows PowerShell 验证进程取消。

| 项目 | 职责 |
| --- | --- |
| `src/Cuelify.Core` | 媒体接口、分片规划、时间轴、cue 构建、提示词与对齐、SRT 序列化 |
| `src/Cuelify.Infrastructure` | Process/FFmpeg、Silero、ElevenLabs、HTTP 翻译、本地 Vulkan、原子存储与业务编排 |
| `src/Cuelify.Desktop` | Avalonia 单窗口、Fluent 资源、MVVM、会话凭据、配置与文件对话框 |
| `tests/Cuelify.Tests` | 纯逻辑、fake HTTP、真实 FFmpeg 集成和本地引擎参数/证据判定 |
| `tests/Cuelify.Desktop.Tests` | 配置、命令、取消/关闭、导出和 headless XAML 测试 |

核心与 Infrastructure 为 net10.0 类库；桌面为 net10.0-windows / win-x64。固定依赖包括 Avalonia 11.3.12、CommunityToolkit.Mvvm 8.4.2、LLamaSharp 与 Vulkan Windows 后端 0.27.0、ONNX Runtime 1.30.0。版本以项目文件和依赖锁文件为准。

## 数据流与约束

识别、翻译、缓存和桌面状态的实现说明统一在[当前架构](agents/architecture.md)维护；不可破坏的时间轴、输出与凭据契约在[产品范围](agents/product.md)维护。修改这些部分时同步相关测试，不另造处理管线。

## 构建与测试

以下命令按改动内容和风险选择，在仓库根目录执行；提交或 amend 不要求自动运行这些命令：

```powershell
dotnet restore Cuelify.slnx --locked-mode
dotnet build Cuelify.slnx -c Release --no-restore
dotnet test Cuelify.slnx -c Release --no-build --logger 'trx;LogFileName=tests.trx'
```

测试自动生成临时媒体，结束后清理。HTTP 和桌面业务替身不调用付费服务、不加载真实 GGUF；Vulkan 合成日志只检查证据判定逻辑。可设置 `CUELIFY_UI_ARTIFACTS` 到 Git 忽略的输出目录保存 headless 截图。

依赖变化时先常规还原更新对应锁文件，再执行 locked restore；不要为消除告警而关闭依赖审计。解决方案只包含正式应用和测试项目。

## 发布

```powershell
dotnet publish src/Cuelify.Desktop/Cuelify.Desktop.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish/win-x64
pwsh -File scripts/Verify-Release.ps1
```

检查脚本要求 PowerShell 7，是开发工具，桌面用户无需安装。它核对必要资产、Silero 哈希、Windows x64 原生目录、包内 .NET 运行时与正常启动/退出；不调用服务，不等同于无 SDK 机器或 GPU 生成验收。

发布目录包含 Silero ONNX、.NET 自包含运行时、ONNX CPU、Vulkan/Skia/ANGLE 原生库、README、项目 MIT `LICENSE`、开发/排障文档和第三方许可。FFmpeg 与 GGUF 由用户准备。`Directory.Build.targets` 过滤 NuGet 原生内容，只保留 win-x64。缺少 Silero 则发布明确失败。

项目自有代码与文档采用 [MIT](../LICENSE)。`licenses/packages.json` 记录依赖来源、版权和许可文本映射；版本变更时同步核对相应许可及第三方声明，保持全文完整。原生间接组件的许可选择与源码获取说明按实际发行构建核对，不能只以主库 MIT 判定整个发行包的义务。

## 模型资源

仓库包含固定 [Silero VAD](https://github.com/snakers4/silero-vad) ONNX，来源提交 `1e261b036686cd0017d500ee96acd1c4ba572a9d` 的 `src/silero_vad/data/silero_vad.onnx`，MIT 许可。文件 SHA-256：

```text
1a153a22f4509e292a94e67d6f9b85e8deb25b4988682b7e174c65279d8788e3
```

GGUF 不纳入版本控制，唯一文件和哈希见 [README](../README.md#本地模型)。可放在忽略的 `assets/models/`，也可在窗口选择其他目录下的该官方文件。程序不提供自动下载。

## 真实验证

使用窗口中的连接测试与真实媒体流程验证云端服务和本地模型。新请求需要对应 Key，可能计费；缓存命中应单独记录，不能充当真实推理。确认 Vulkan 设备、非零 GPU layer offload、实际生成、取消与释放，不以设备发现或 CPU 生成代替。

发行验收应包含独立无 SDK 的 Windows 11 x64、原生文件对话框/拖放、键盘焦点、明暗主题、100%/150%/200% DPI、长媒体听音与播放器加载 SRT。当前覆盖情况和待验收项统一在[验证状态](agents/status.md)维护。报告和运行日志保存到仓库外或忽略的 `artifacts/`。
