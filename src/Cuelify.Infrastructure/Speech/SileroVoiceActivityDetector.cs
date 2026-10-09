using System.Buffers.Binary;
using Cuelify.Core.Speech;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Cuelify.Infrastructure.Speech;

public sealed class SileroVoiceActivityDetector : IVoiceActivityDetector
{
    private readonly string _modelPath;
    private readonly SileroVadOptions _options;

    public SileroVoiceActivityDetector(string modelPath, SileroVadOptions? options = null)
    {
        _modelPath = Path.GetFullPath(modelPath);
        _options = options ?? new SileroVadOptions();
        _options.Validate();
    }

    public Task<IReadOnlyList<SpeechRegion>> DetectAsync(Stream pcm16Khz, CancellationToken cancellationToken) =>
        Task.Run(() => DetectCoreAsync(pcm16Khz, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<SpeechRegion>> DetectCoreAsync(Stream pcm16Khz, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pcm16Khz);
        if (!pcm16Khz.CanRead) throw new ArgumentException("PCM 流不可读。");
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_modelPath)) throw new FileNotFoundException("找不到 Silero ONNX 模型。", _modelPath);

        // 每次检测使用独立状态与 session；session/options 必须真正释放。
        using var sessionOptions = new SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = 1 };
        sessionOptions.AppendExecutionProvider_CPU();
        using var session = new InferenceSession(_modelPath, sessionOptions);
        var state = new float[2 * 128];
        var input = new float[64 + SpeechRegionBuilder.WindowSamples];
        var bytes = new byte[SpeechRegionBuilder.WindowSamples * 2];
        var builder = new SpeechRegionBuilder(_options);

        while (true)
        {
            var read = 0;
            while (read < bytes.Length)
            {
                var count = await pcm16Khz.ReadAsync(bytes.AsMemory(read), cancellationToken);
                if (count == 0) break;
                read += count;
            }
            if (read == 0) break;
            if (read % 2 != 0) throw new InvalidDataException("16 位 PCM 数据有不完整的尾部样本。");
            var validSamples = read / 2;
            Array.Clear(input, 64, SpeechRegionBuilder.WindowSamples);
            for (var i = 0; i < validSamples; i++)
                input[64 + i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2, 2)) / 32768f;

            cancellationToken.ThrowIfCancellationRequested();
            var inputs = new[]
            {
                NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(input, [1, input.Length])),
                NamedOnnxValue.CreateFromTensor("state", new DenseTensor<float>(state, [2, 1, 128])),
                NamedOnnxValue.CreateFromTensor("sr", new DenseTensor<long>(new long[] { SpeechRegionBuilder.SampleRate }, [1]))
            };
            using var outputs = session.Run(inputs);
            var probability = outputs.Single(output => output.Name == "output").AsTensor<float>()[0];
            state = outputs.Single(output => output.Name == "stateN").AsTensor<float>().ToArray();
            Array.Copy(input, input.Length - 64, input, 0, 64);
            builder.Add(probability, validSamples);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return builder.Complete();
    }
}
