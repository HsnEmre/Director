using System.Text.Json.Nodes;
using Director.Services.Interfaces;

namespace Director.WanGp;

public sealed class WanGpAudioInputContractResolver : IWanGpAudioInputContractResolver
{
    public const string KugelAudioDefaultVoiceKey = "kugelaudio_default";
    public const string KugelAudioDefaultVoiceDisplayName = "KugelAudio Varsayilan Sesi";

    public WanGpAudioInputContract Resolve(WanGpModelInfo model, WanGpModelSchema schema)
    {
        var contract = new WanGpAudioInputContract();
        var combined = schema.RawSchema.ToJsonString() + schema.DefaultSettings.ToJsonString();
        var isKugelAudio = model.ModelType.Contains("kugel", StringComparison.OrdinalIgnoreCase) ||
            model.DisplayName.Contains("KugelAudio", StringComparison.OrdinalIgnoreCase);
        contract.TextKey = FindKey(schema, "text", "script", "prompt", "audio_prompt") ?? string.Empty;
        contract.VoiceKey = FindKey(schema, "voice", "voice_preset", "speaker", "speaker_id", "preset") ?? string.Empty;
        contract.LanguageKey = FindKey(schema, "language", "lang");
        contract.SeedKey = FindKey(schema, "seed");
        contract.CfgScaleKey = FindKey(schema, "cfg_scale", "guidance_scale");
        contract.DoSampleKey = FindKey(schema, "do_sample");
        contract.TemperatureKey = FindKey(schema, "temperature");
        contract.MaxNewTokensKey = FindKey(schema, "max_new_tokens", "sampling_steps");
        contract.OutputFormatKey = FindKey(schema, "output_format", "format", "audio_format");
        contract.SupportsDialogue = combined.Contains("speaker", StringComparison.OrdinalIgnoreCase) || combined.Contains("dialogue", StringComparison.OrdinalIgnoreCase);
        contract.SupportsRawReferenceAudio = !isKugelAudio &&
            (combined.Contains("reference_audio", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("audio_reference", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("audio_guide", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("audio_prompt_type", StringComparison.OrdinalIgnoreCase));
        contract.AvailableVoices = ExtractVoices(schema.RawSchema).Concat(ExtractVoices(schema.DefaultSettings))
            .GroupBy(voice => voice.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        contract.SupportsVoicePreset = !string.IsNullOrWhiteSpace(contract.VoiceKey) && contract.AvailableVoices.Count > 0;
        contract.SupportsDeterministicGeneration = !string.IsNullOrWhiteSpace(contract.SeedKey);
        if (!contract.SupportsVoicePreset && isKugelAudio && !string.IsNullOrWhiteSpace(contract.TextKey))
        {
            contract.UsesImplicitDefaultVoice = true;
            contract.AvailableVoices =
            [
                new WanGpVoicePreset
                {
                    Key = KugelAudioDefaultVoiceKey,
                    DisplayName = KugelAudioDefaultVoiceDisplayName
                }
            ];
        }

        contract.Evidence.Add("ModelSchema");
        contract.Evidence.Add("DefaultSettings");
        if (contract.UsesImplicitDefaultVoice)
        {
            contract.Evidence.Add("KugelAudioImplicitDefaultVoice");
        }

        if (!string.IsNullOrWhiteSpace(contract.TemperatureKey))
        {
            contract.Evidence.Add("TemperatureSetting");
        }

        if (!string.IsNullOrWhiteSpace(contract.CfgScaleKey))
        {
            contract.Evidence.Add("CfgScaleSetting");
        }

        contract.IsValidated = !string.IsNullOrWhiteSpace(contract.TextKey) &&
            (contract.SupportsVoicePreset || contract.UsesImplicitDefaultVoice);
        if (!contract.IsValidated)
        {
            contract.FailureReason = $"KugelAudio audio sozlesmesi cozumlenemedi. model_type={model.ModelType}; textKey={contract.TextKey}; voiceKey={contract.VoiceKey}; voices={contract.AvailableVoices.Count}";
        }

        return contract;
    }

    private static string? FindKey(WanGpModelSchema schema, params string[] keys)
    {
        return WanGpVideoInputContractResolver.FindPropertyName(schema.RawSchema, keys)
            ?? keys.FirstOrDefault(key => schema.DefaultSettings.ContainsKey(key));
    }

    private static IEnumerable<WanGpVoicePreset> ExtractVoices(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (pair.Key.Contains("voice", StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.Contains("speaker", StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.Contains("preset", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var voice in ReadVoiceValues(pair.Value))
                    {
                        yield return voice;
                    }
                }

                foreach (var voice in ExtractVoices(pair.Value))
                {
                    yield return voice;
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                foreach (var voice in ExtractVoices(item))
                {
                    yield return voice;
                }
            }
        }
    }

    private static IEnumerable<WanGpVoicePreset> ReadVoiceValues(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                var value = item?.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(value) && value.Length <= 120)
                {
                    yield return new WanGpVoicePreset { Key = value, DisplayName = value };
                }
            }
        }
        else if (node is JsonValue)
        {
            var value = node.ToString();
            if (!string.IsNullOrWhiteSpace(value) && value.Length <= 120)
            {
                yield return new WanGpVoicePreset { Key = value, DisplayName = value };
            }
        }
    }
}
