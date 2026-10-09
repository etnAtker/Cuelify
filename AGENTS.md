# Cuelify Agent 入口

Agent 专用文档统一在 `docs/agents/`。先读 [Agent 文档索引](docs/agents/README.md)、[产品范围](docs/agents/product.md)、[Agent 工作指南](docs/agents/agent-guide.md)，再读相关源码和测试；架构、命令、决策与验证状态均由索引引导。UI 改动必须读 [UI 与文案规约](docs/agents/ui-guidelines.md)。

## 核心规约

- 面向用户的说明和文档使用中文。编码前理解职责与数据流，提交简短修改计划并取得明确授权；已授权范围内持续实施，范围变化再对齐。
- 长期目标跨平台，当前发行和正式验收为 Windows 11 x64；凭证层使用主密码加密，不依赖系统凭据存储；.NET 10、Avalonia、CommunityToolkit.Mvvm。单个本地媒体经 FFmpeg、Silero CPU VAD、ElevenLabs 和翻译，仅导出 SRT；不扩展播放器、队列、烧录或通用模型管理。
- FFmpeg/ffprobe 经 ICommandRunner + Process；翻译保留兼容 API、DeepSeek 和本地三条路径。本地唯一为官方 Hy-MT2-1.8B-Q4_K_M.gguf，进程内 LLamaSharp + Vulkan；禁止 CUDA、CPU-only 回退、外部模型服务/CLI。验收要求实际生成及非零 GPU layer offload。
- 保持 cue ID、数量、顺序和原时码；失败译文不能用原文冒充。ASR/翻译缓存分离，JSON 原子保存；修改配置不影响已完成任务的快照和导出。
- API Key 不进普通 JSON、缓存身份、日志、预览或命令行参数；不在仓库记录本机凭据来源。真实调用、缓存、模拟与历史证据分别报告。
- 复用现有分层、编排、校验、错误映射和设计资源；不引入过度架构，不用假实现绕过依赖或环境限制。改变冻结决策先说明证据和影响，获得批准后更新文档。
- UI 沿用图标导航与三步工作台，设置/日志独立；文案描述用户动作和真实状态，不把工程规约或验收术语写进日常界面。上传、计费、密钥保存和覆盖文件等必要信息简洁说明。
- 按改动内容和风险选择验证；提交或 amend 本身不要求运行还原、构建、测试或发行检查。需要验证时参考开发指南，如实报告实际结果和未验证项；不能把模拟或旧结果当本轮真实验收。
- 保留用户已有改动和必要缓存；产物放忽略目录或仓库外，不恢复旧阶段/Spike 杂项。提交保持单一逻辑范围，Conventional Commits 描述使用中文。

详细工作流见 [Agent 指南](docs/agents/agent-guide.md)，当前后续起点见 [验证状态](docs/agents/status.md)。长期规则维护在对应文档，AGENTS 不堆积阶段日志。
