using System.Security.Cryptography;
using System.Text;
using Director.Services.Interfaces;

namespace Director.WanGp;

public sealed class WanGpAudioRequestBuilder : IWanGpAudioRequestBuilder
{
    private readonly IWanGpClient _wanGpClient;
    private readonly IWanGpAudioInputContractResolver _contractResolver;

    public WanGpAudioRequestBuilder(IWanGpClient wanGpClient, IWanGpAudioInputContractResolver contractResolver)
    {
        _wanGpClient = wanGpClient;
        _contractResolver = contractResolver;
    }

    public async Task<WanGpAudioRequestBuildResult> BuildAsync(WanGpAudioGenerationRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.TurkishText))
        {
            throw new InvalidOperationException("Turkce konusma metni bos olamaz.");
        }

        var schema = await _wanGpClient.GetModelSchemaAsync(request.ModelType, cancellationToken)
            ?? throw new InvalidOperationException("WanGP audio model schema alinamadi.");
        var contract = request.InputContract ?? _contractResolver.Resolve(new WanGpModelInfo
        {
            ModelType = request.ModelType,
            MainOutput = "audio",
            Inputs = "text",
            Outputs = "audio"
        }, schema);

        if (!contract.IsValidated)
        {
            throw new InvalidOperationException(contract.FailureReason);
        }

        if (contract.SupportsVoicePreset &&
            !contract.AvailableVoices.Any(voice => string.Equals(voice.Key, request.VoicePresetKey, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Secili voice preset KugelAudio schema listesinde yok.");
        }

        var source = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["model_type"] = request.ModelType,
            [contract.TextKey] = request.TurkishText
        };

        if (contract.SupportsVoicePreset && !string.IsNullOrWhiteSpace(contract.VoiceKey))
        {
            source[contract.VoiceKey] = request.VoicePresetKey;
        }

        if (!string.IsNullOrWhiteSpace(contract.LanguageKey))
        {
            source[contract.LanguageKey] = request.Language;
        }

        if (!string.IsNullOrWhiteSpace(contract.SeedKey) && request.Seed is int seed)
        {
            source[contract.SeedKey] = seed;
        }

        if (!string.IsNullOrWhiteSpace(contract.CfgScaleKey) && request.CfgScale is double cfg)
        {
            source[contract.CfgScaleKey] = cfg;
        }

        if (!string.IsNullOrWhiteSpace(contract.DoSampleKey))
        {
            source[contract.DoSampleKey] = request.DoSample;
        }

        if (!string.IsNullOrWhiteSpace(contract.TemperatureKey) && request.Temperature is double temperature)
        {
            source[contract.TemperatureKey] = temperature;
        }

        if (!string.IsNullOrWhiteSpace(contract.MaxNewTokensKey) && request.MaxNewTokens is int maxNewTokens)
        {
            source[contract.MaxNewTokensKey] = maxNewTokens;
        }

        if (!string.IsNullOrWhiteSpace(contract.OutputFormatKey))
        {
            source[contract.OutputFormatKey] = "wav";
        }

        return new WanGpAudioRequestBuildResult
        {
            Source = source,
            Contract = contract,
            TextHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.TurkishText))).ToLowerInvariant()
        };
    }
}
