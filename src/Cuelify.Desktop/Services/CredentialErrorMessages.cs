namespace Cuelify.Desktop.Services;

internal static class CredentialErrorMessages
{
    // 此路径不输出异常详情，避免损坏的文件内容或密码进入诊断。
    public static string FromException(Exception exception) => exception switch
    {
        ArgumentException or InvalidDataException or InvalidOperationException => exception.Message,
        UnauthorizedAccessException => "无法访问凭证文件，请检查应用数据目录的读写权限。",
        IOException => "无法读写凭证文件。请检查读写权限，或关闭其他正在修改凭证的窗口后重试。",
        OperationCanceledException => "操作已取消。",
        _ => "凭证操作未完成，请重试。"
    };
}
