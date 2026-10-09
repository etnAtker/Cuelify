using System.Net.Http.Headers;
using Cuelify.Core.Speech;

namespace Cuelify.Infrastructure.Speech;

public sealed class ElevenLabsAsrClient : IAsrClient
{
    private readonly HttpClient _httpClient;
    private readonly Func<string?> _apiKeyProvider;
    private readonly ElevenLabsAsrOptions _options;

    public ElevenLabsAsrClient(HttpClient httpClient, Func<string?> apiKeyProvider, ElevenLabsAsrOptions? options = null)
    {
        _httpClient = httpClient;
        _apiKeyProvider = apiKeyProvider;
        _options = options ?? new ElevenLabsAsrOptions();
        _options.Validate();
    }

    public async Task<AsrTranscript> TranscribeAsync(string audioFilePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = _apiKeyProvider();
        if (string.IsNullOrWhiteSpace(key) || key.Contains('\r') || key.Contains('\n'))
            throw new ServiceCredentialException("ElevenLabs");
        await using var file = File.OpenRead(audioFilePath);
        if (file.Length == 0 || file.Length > _options.MaximumUploadBytes)
            throw new InvalidDataException("音频文件为空或超过上传上限；应重新编码或分片，不能重试同一个过大文件。");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.elevenlabs.io/v1/speech-to-text");
        request.Headers.Add("xi-api-key", key);
        using var multipart = new MultipartFormDataContent();
        using var audioContent = new StreamContent(file);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        multipart.Add(audioContent, "file", Path.GetFileName(audioFilePath));
        multipart.Add(new StringContent(_options.ModelId), "model_id");
        multipart.Add(new StringContent("word"), "timestamps_granularity");
        multipart.Add(new StringContent("false"), "diarize");
        multipart.Add(new StringContent("false"), "use_multi_channel");
        multipart.Add(new StringContent("false"), "tag_audio_events");
        multipart.Add(new StringContent(_options.FileFormat), "file_format");
        if (_options.LanguageCode is not null) multipart.Add(new StringContent(_options.LanguageCode), "language_code");
        request.Content = multipart;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta;
            if (retryAfter is null && response.Headers.RetryAfter?.Date is { } date)
                retryAfter = date > DateTimeOffset.UtcNow ? date - DateTimeOffset.UtcNow : TimeSpan.Zero;
            throw new AsrServiceException(response.StatusCode, retryAfter);
        }
        var json = await response.Content.ReadAsStringAsync(timeout.Token);
        return ElevenLabsTranscriptParser.Parse(json);
    }
}
