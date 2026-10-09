using System.Net;
using Cuelify.Infrastructure;
using Cuelify.Infrastructure.Speech;
using Cuelify.Infrastructure.Transcription;
using Cuelify.Infrastructure.Translation;
using Cuelify.Infrastructure.Translation.Local;

namespace Cuelify.Desktop.Services;

internal static class UserErrorMessages
{
    public static string FromException(Exception exception) => exception switch
    {
        ServiceCredentialException credential => credential.Message,
        TranscriptionFailedException failure => $"{failure.FailedChunks.Count} 个音频片段识别失败。" +
            string.Join(" ", failure.Categories.Values.Select(value => FromCategory(value, "ElevenLabs")).Distinct()) +
            " 解决问题后可继续处理，已完成的识别无需重复请求。",
        AsrServiceException service => Http(service.StatusCode, "ElevenLabs"),
        TranslationServiceException service => Http(service.StatusCode, "翻译服务"),
        LocalTranslationException local => Local(local.Category),
        ArgumentException => exception.Message,
        FormatException or OverflowException => "数值格式无效，请检查输入的数字。",
        TimeoutException => "处理超时，请检查网络或调大超时时间后重试。",
        HttpRequestException => "无法连接服务，请检查网络和服务地址后重试。",
        FileNotFoundException file => string.IsNullOrWhiteSpace(file.FileName) ? "找不到所需文件，请检查文件路径。" : $"找不到文件：{Path.GetFileName(file.FileName)}。请检查文件路径。",
        InvalidDataException => exception.Message,
        System.ComponentModel.Win32Exception { NativeErrorCode: 2 or 3 } => "无法启动音频处理程序，请检查 FFmpeg 和 ffprobe 的路径。",
        UnauthorizedAccessException => "无法访问文件，请检查文件夹的读写权限。",
        IOException => "无法读写文件或处理音频，请检查文件路径、读写权限和 FFmpeg 配置。",
        _ => "操作未完成，请查看运行日志了解详情。"
    };

    public static string FromCategory(string category, string service = "翻译服务")
    {
        if (category.StartsWith("HTTP ", StringComparison.Ordinal) && int.TryParse(category.AsSpan(5), out var code)) return Http((HttpStatusCode)code, service);
        if (category.StartsWith("Local:", StringComparison.Ordinal)) return Local(category[6..]);
        return category switch
        {
            "网络失败" => $"无法连接{service}，请检查网络和服务地址。",
            "超时" or "请求超时" or "取消或超时" => "请求未完成，请稍后重试或调大超时时间。",
            "配置或凭据无效" or "会话凭据或处理状态无效" => $"请检查{service}的 API 密钥和设置。",
            "译文或 ID 校验失败" or "译文或缓存校验失败" => "返回的译文不完整或格式不正确，请检查提示词后重试。",
            "数据或缓存校验失败" => "识别数据不完整或格式不正确，请查看运行日志。",
            "文件或外部命令失败" => "音频处理失败，请检查文件路径和 FFmpeg 配置。",
            "原有译文未完成，本次未选中重翻" => "还有其他字幕未完成，请继续处理。",
            _ when category.StartsWith("请填写有效的", StringComparison.Ordinal) => category,
            _ => "处理未完成，请查看运行日志。"
        };
    }

    private static string Http(HttpStatusCode code, string service) => code switch
    {
        HttpStatusCode.Unauthorized => $"{service}认证失败，请检查 API 密钥。",
        HttpStatusCode.Forbidden => $"没有权限使用{service}，请检查密钥权限和账户状态。",
        HttpStatusCode.PaymentRequired => $"{service}账户需要付费，请检查账户余额或订阅。",
        HttpStatusCode.TooManyRequests => $"{service}请求过于频繁或额度不足，请检查账户额度并稍后重试。",
        HttpStatusCode.NotFound => $"找不到{service}的接口或模型，请检查服务地址和模型名称。",
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => $"{service}未接受请求，请检查模型、提示词和高级参数。",
        HttpStatusCode.RequestEntityTooLarge => $"{service}收到的数据过大，请减小音频片段或翻译批次。",
        HttpStatusCode.RequestTimeout => $"{service}请求超时，请稍后重试。",
        _ when (int)code >= 500 => $"{service}暂时不可用，请稍后重试。",
        _ => $"{service}请求失败（HTTP {(int)code}），请查看运行日志。"
    };

    private static string Local(string category) => category switch
    {
        "GpuMemory" => "显存不足，请关闭占用显卡的程序，或降低 GPU 卸载层数和上下文长度。",
        "ModelIdentity" or "ModelArchitecture" => "模型文件不完整或与所选模型不符，请重新下载或选择对应模型文件。",
        "NativeLibrary" => "本地翻译组件加载失败，请重新解压完整的程序包。",
        "VulkanOffload" => "无法使用显卡运行模型，请检查 Vulkan 支持和显卡驱动。",
        "WindowsX64Required" => "本地翻译需要 Windows x64 系统。",
        _ => "本地翻译失败，请检查显卡驱动和模型文件，并查看运行日志。"
    };
}
