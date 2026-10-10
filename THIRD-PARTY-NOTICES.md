# 第三方许可与声明

Cuelify 使用下列第三方组件。独立许可和第三方声明全文保存在 `licenses/`，相同内容共用一份文件；版权、来源提交和文本映射见 `licenses/packages.json`。

| 组件 / 版本 | 版权声明 | 许可与第三方声明 |
| --- | --- | --- |
| Avalonia/11.3.12 | Copyright 2013-2026 © The AvaloniaUI Project | [Avalonia-NuGet-LICENSE.txt](licenses/Avalonia-NuGet-LICENSE.txt) |
| Avalonia.Angle.Windows.Natives/2.1.25547.20250602 | Copyright 2013-2025 © The AvaloniaUI Project | [Avalonia.Angle.Windows.Natives-NuGet-LICENSE.txt](licenses/Avalonia.Angle.Windows.Natives-NuGet-LICENSE.txt) |
| Avalonia.Remote.Protocol/11.3.12 | Copyright 2013-2026 © The AvaloniaUI Project | [Avalonia-NuGet-LICENSE.txt](licenses/Avalonia-NuGet-LICENSE.txt) |
| Avalonia.Skia/11.3.12 | Copyright 2013-2026 © The AvaloniaUI Project | [Avalonia-NuGet-LICENSE.txt](licenses/Avalonia-NuGet-LICENSE.txt) |
| Avalonia.Themes.Fluent/11.3.12 | Copyright 2013-2026 © The AvaloniaUI Project | [Avalonia-NuGet-LICENSE.txt](licenses/Avalonia-NuGet-LICENSE.txt) |
| Avalonia.Win32/11.3.12 | Copyright 2013-2026 © The AvaloniaUI Project | [Avalonia-NuGet-LICENSE.txt](licenses/Avalonia-NuGet-LICENSE.txt) |
| CommunityToolkit.Mvvm/8.4.2 | (c) .NET Foundation and Contributors. All rights reserved. | [CommunityToolkit.HighPerformance-NuGet-License.md](licenses/CommunityToolkit.HighPerformance-NuGet-License.md)<br>[CommunityToolkit.HighPerformance-NuGet-ThirdPartyNotices.txt](licenses/CommunityToolkit.HighPerformance-NuGet-ThirdPartyNotices.txt) |
| HarfBuzzSharp/8.3.1.1 | © Microsoft Corporation. All rights reserved. | [HarfBuzzSharp-NuGet-LICENSE.txt](licenses/HarfBuzzSharp-NuGet-LICENSE.txt) |
| HarfBuzzSharp.NativeAssets.Win32/8.3.1.1 | © Microsoft Corporation. All rights reserved. | [HarfBuzzSharp-NuGet-LICENSE.txt](licenses/HarfBuzzSharp-NuGet-LICENSE.txt)<br>[HarfBuzzSharp.NativeAssets.Win32-NuGet-THIRD-PARTY-NOTICES.txt](licenses/HarfBuzzSharp.NativeAssets.Win32-NuGet-THIRD-PARTY-NOTICES.txt) |
| MicroCom.Runtime/0.11.0 | Copyright 2021 © Nikita Tsukanov | [MicroCom.Runtime-NuGet-LICENSE.txt](licenses/MicroCom.Runtime-NuGet-LICENSE.txt) |
| Microsoft.ML.OnnxRuntime/1.30.0 | © Microsoft Corporation. All rights reserved. | [Microsoft.ML.OnnxRuntime-NuGet-LICENSE.txt](licenses/Microsoft.ML.OnnxRuntime-NuGet-LICENSE.txt)<br>[Microsoft.ML.OnnxRuntime-NuGet-ThirdPartyNotices.txt](licenses/Microsoft.ML.OnnxRuntime-NuGet-ThirdPartyNotices.txt) |
| Microsoft.ML.OnnxRuntime.Managed/1.30.0 | © Microsoft Corporation. All rights reserved. | [Microsoft.ML.OnnxRuntime-NuGet-LICENSE.txt](licenses/Microsoft.ML.OnnxRuntime-NuGet-LICENSE.txt)<br>[Microsoft.ML.OnnxRuntime-NuGet-ThirdPartyNotices.txt](licenses/Microsoft.ML.OnnxRuntime-NuGet-ThirdPartyNotices.txt) |
| SkiaSharp/2.88.9 | © Microsoft Corporation. All rights reserved. | [HarfBuzzSharp-NuGet-LICENSE.txt](licenses/HarfBuzzSharp-NuGet-LICENSE.txt)<br>[HarfBuzzSharp.NativeAssets.Win32-NuGet-THIRD-PARTY-NOTICES.txt](licenses/HarfBuzzSharp.NativeAssets.Win32-NuGet-THIRD-PARTY-NOTICES.txt) |
| SkiaSharp.NativeAssets.Win32/2.88.9 | © Microsoft Corporation. All rights reserved. | [HarfBuzzSharp-NuGet-LICENSE.txt](licenses/HarfBuzzSharp-NuGet-LICENSE.txt)<br>[HarfBuzzSharp.NativeAssets.Win32-NuGet-THIRD-PARTY-NOTICES.txt](licenses/HarfBuzzSharp.NativeAssets.Win32-NuGet-THIRD-PARTY-NOTICES.txt) |
| System.Numerics.Tensors/9.0.0 | © Microsoft Corporation. All rights reserved. | [Microsoft.NETCore.App.Runtime.win-x64-NuGet-LICENSE.txt](licenses/Microsoft.NETCore.App.Runtime.win-x64-NuGet-LICENSE.txt)<br>[System.Numerics.Tensors-NuGet-THIRD-PARTY-NOTICES.txt](licenses/System.Numerics.Tensors-NuGet-THIRD-PARTY-NOTICES.txt) |
| Microsoft.NETCore.App.Runtime.win-x64/10.0.9 | © Microsoft Corporation. All rights reserved. | [Microsoft.Bcl.AsyncInterfaces-NuGet-THIRD-PARTY-NOTICES.txt](licenses/Microsoft.Bcl.AsyncInterfaces-NuGet-THIRD-PARTY-NOTICES.txt)<br>[Microsoft.NETCore.App.Runtime.win-x64-NuGet-LICENSE.txt](licenses/Microsoft.NETCore.App.Runtime.win-x64-NuGet-LICENSE.txt) |

## 上游许可与资源

- Avalonia 原始许可：[全文](licenses/Avalonia-LICENSE.md)，来源提交 `37fbd9655cc581ff5b1c6b1fb1be4e3118c889d0`。
- llama.cpp 许可参考：[全文](licenses/llama.cpp-LICENSE.txt)，参考文本来源提交 `3f7c29d318e317b63f54c558bc69803963d7d88c`。运行程序不随应用发行，下载时另保存所安装 tag 的完整 LICENSE，实际构建号和包 SHA-256 记录在安装目录的 `installation.json`。
- Silero VAD ONNX：[全文](licenses/Silero-LICENSE.txt)，来源提交 `1e261b036686cd0017d500ee96acd1c4ba572a9d`；该资源包含在源码及发行包中。

.NET Runtime、ONNX Runtime、SkiaSharp/HarfBuzz、ANGLE 及其他依赖的附带第三方声明按上述映射保存。NuGet 清单中的 License 字段保留上游元数据；文件名形式的值指上游许可文件。

llama.cpp、GGUF 和 FFmpeg 不随包提供。llama.cpp 官方运行包的配套文件完整保留，不重新组合第三方 DLL；运行包按其上游许可使用。应用支持从官方仓库下载 [Hy-MT2-1.8B](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF) 和 [Hy-MT2-7B](https://huggingface.co/tencent/Hy-MT2-7B-GGUF)，这些仓库当前声明 Apache-2.0，使用者按模型来源许可使用；FFmpeg 由用户准备。Cuelify 自有代码与文档采用根目录 [MIT 许可证](LICENSE)；本文件及 `licenses/` 保留第三方各自的许可，不受项目许可证替代。
