namespace Cuelify.Infrastructure;

public sealed class ServiceCredentialException(string service) : InvalidOperationException($"请填写有效的{service} API 密钥。")
{
    public string Service { get; } = service;
}
