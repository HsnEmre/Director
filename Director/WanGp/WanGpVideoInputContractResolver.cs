using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Director.Services.Interfaces;

namespace Director.WanGp;

public sealed class WanGpVideoInputContractResolver : IWanGpVideoInputContractResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public Task<WanGpVideoInputContract> ResolveAsync(
        WanGpModelInfo model,
        WanGpModelSchema? schema,
        IReadOnlyDictionary<string, object?> defaults,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var contract = new WanGpVideoInputContract
        {
            SupportsImageToVideo = model.SupportsImageToVideo || ContainsAny(model.RawMetadata, "image_to_video", "video"),
            SupportsStartImage = model.SupportsStartImage,
            SupportsReferenceImage = model.SupportsReferenceImage
        };

        if (model.SupportsImageToVideo ||
            model.Inputs.Contains("image", StringComparison.OrdinalIgnoreCase) ||
            model.Outputs.Contains("video", StringComparison.OrdinalIgnoreCase) ||
            model.RawMetadata.ToJsonString().Contains("image_to_video", StringComparison.OrdinalIgnoreCase))
        {
            contract.Evidence.Add(WanGpVideoInputContractEvidence.ModelMetadata);
            contract.SupportsImageToVideo = true;
        }

        if (TryResolveFromSchema(schema, contract))
        {
            contract.Evidence.Add(WanGpVideoInputContractEvidence.ModelSchema);
        }

        if (TryResolveFromDefaults(defaults, contract))
        {
            contract.Evidence.Add(WanGpVideoInputContractEvidence.DefaultSettings);
        }

        if (TryResolveLtxCompatibility(model, contract))
        {
            contract.Evidence.Add(WanGpVideoInputContractEvidence.ArchitectureCompatibilityProfile);
        }

        contract.IsValidated = contract.SupportsImageToVideo &&
            contract.SupportsStartImage &&
            !string.IsNullOrWhiteSpace(contract.StartImageKey) &&
            !string.IsNullOrWhiteSpace(contract.StartImageModeKey) &&
            !string.IsNullOrWhiteSpace(contract.StartImageModeValue);

        if (!contract.IsValidated)
        {
            contract.FailureReason = BuildFailureReason(model, contract, schema, defaults);
        }

        WriteDiagnostics(model, contract, defaults);
        return Task.FromResult(contract);
    }

    private static bool TryResolveFromSchema(WanGpModelSchema? schema, WanGpVideoInputContract contract)
    {
        if (schema is null)
        {
            return false;
        }

        var found = false;
        var startKey = FindPropertyName(schema.RawSchema, "image_start", "start_image", "init_image", "source_image", "input_image");
        if (!string.IsNullOrWhiteSpace(startKey))
        {
            contract.StartImageKey = startKey;
            contract.SupportsStartImage = true;
            found = true;
        }

        var modeKey = FindPropertyName(schema.RawSchema, "image_prompt_type");
        if (!string.IsNullOrWhiteSpace(modeKey))
        {
            contract.StartImageModeKey = modeKey;
            contract.StartImageModeValue = ResolveStartModeValue(schema.RawSchema);
            found = true;
        }

        var referenceKey = FindPropertyName(schema.RawSchema, "image_reference", "reference_image", "ref_image");
        if (!string.IsNullOrWhiteSpace(referenceKey))
        {
            contract.ReferenceImageKey = referenceKey;
            contract.SupportsReferenceImage = true;
            found = true;
        }

        return found;
    }

    private static bool TryResolveFromDefaults(IReadOnlyDictionary<string, object?> defaults, WanGpVideoInputContract contract)
    {
        var found = false;
        if (TryGetDefaultKey(defaults, out var startKey, "image_start", "start_image", "init_image", "source_image", "input_image"))
        {
            contract.StartImageKey = startKey;
            contract.SupportsStartImage = true;
            found = true;
        }

        if (TryGetDefaultKey(defaults, out var modeKey, "image_prompt_type"))
        {
            contract.StartImageModeKey = modeKey;
            contract.StartImageModeValue = ReadDefaultString(defaults, modeKey) is { Length: > 0 } value ? value : "S";
            found = true;
        }

        if (TryGetDefaultKey(defaults, out var referenceKey, "image_reference", "reference_image", "ref_image"))
        {
            contract.ReferenceImageKey = referenceKey;
            contract.SupportsReferenceImage = true;
            found = true;
        }

        return found;
    }

    private static bool TryResolveLtxCompatibility(WanGpModelInfo model, WanGpVideoInputContract contract)
    {
        var raw = model.RawMetadata.ToJsonString();
        var isLtx2 = model.ModelType.Contains("ltx2_22B", StringComparison.OrdinalIgnoreCase) ||
            model.Architecture.Contains("ltx2_22B", StringComparison.OrdinalIgnoreCase) ||
            model.BaseModelType.Contains("ltx2_22B", StringComparison.OrdinalIgnoreCase) ||
            model.Family.Contains("ltx2", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("ltx2_22B", StringComparison.OrdinalIgnoreCase);

        if (!isLtx2 || !contract.SupportsImageToVideo)
        {
            return false;
        }

        contract.SupportsStartImage = true;
        contract.StartImageKey = "image_start";
        contract.StartImageModeKey = "image_prompt_type";
        contract.StartImageModeValue = "S";
        contract.StartImageValueShape = WanGpVideoStartImageValueShape.StringPath;
        return true;
    }

    private static string ResolveStartModeValue(JsonObject schema)
    {
        var json = schema.ToJsonString();
        return json.Contains("Start image", StringComparison.OrdinalIgnoreCase) ||
            json.Contains("\"S\"", StringComparison.OrdinalIgnoreCase)
                ? "S"
                : string.Empty;
    }

    public static string? FindPropertyName(JsonNode? node, params string[] candidates)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                if (candidates.Any(candidate => string.Equals(property.Key, candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    return property.Key;
                }

                var nested = FindPropertyName(property.Value, candidates);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                var nested = FindPropertyName(item, candidates);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    public static bool HasProperty(JsonNode? node, string key)
    {
        return !string.IsNullOrWhiteSpace(FindPropertyName(node, key));
    }

    private static bool TryGetDefaultKey(IReadOnlyDictionary<string, object?> defaults, out string key, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (defaults.Keys.FirstOrDefault(item => string.Equals(item, candidate, StringComparison.OrdinalIgnoreCase)) is { } found)
            {
                key = found;
                return true;
            }
        }

        key = string.Empty;
        return false;
    }

    private static string ReadDefaultString(IReadOnlyDictionary<string, object?> defaults, string key)
    {
        return defaults.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
    }

    private static bool ContainsAny(JsonNode? node, params string[] values)
    {
        var json = node?.ToJsonString() ?? string.Empty;
        return values.Any(value => json.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildFailureReason(
        WanGpModelInfo model,
        WanGpVideoInputContract contract,
        WanGpModelSchema? schema,
        IReadOnlyDictionary<string, object?> defaults)
    {
        return "Secili model image-to-video destekliyor ancak WanGP Start Image sozlesmesi cozumlenemedi. "
            + $"model_type={model.ModelType}; architecture={model.Architecture}; metadataStart={model.SupportsStartImage}; "
            + $"schemaStart={schema is not null && HasProperty(schema.RawSchema, "image_start")}; "
            + $"defaultsStart={defaults.Keys.Any(key => key.Equals("image_start", StringComparison.OrdinalIgnoreCase))}; "
            + $"evidence={contract.EvidenceText}";
    }

    private static void WriteDiagnostics(
        WanGpModelInfo model,
        WanGpVideoInputContract contract,
        IReadOnlyDictionary<string, object?> defaults)
    {
        if (!model.ModelType.Contains("ltx", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "DirectorWanGpVideoDiagnostics");
        Directory.CreateDirectory(root);
        var summary = new JsonObject
        {
            ["capturedAt"] = DateTime.Now,
            ["modelType"] = model.ModelType,
            ["displayName"] = model.DisplayName,
            ["supportsImageToVideo"] = contract.SupportsImageToVideo,
            ["supportsStartImage"] = contract.SupportsStartImage,
            ["supportsReferenceImage"] = contract.SupportsReferenceImage,
            ["startImageKey"] = contract.StartImageKey,
            ["startImageModeKey"] = contract.StartImageModeKey,
            ["startImageModeValue"] = contract.StartImageModeValue,
            ["startImageValueShape"] = contract.StartImageValueShape.ToString(),
            ["inputContractValidated"] = contract.IsValidated,
            ["evidence"] = new JsonArray(contract.Evidence.Select(item => JsonValue.Create(item.ToString())).ToArray()),
            ["hasImageModeDefault"] = defaults.Keys.Any(key => key.Equals("image_mode", StringComparison.OrdinalIgnoreCase)),
            ["metadataHash"] = Hash(model.RawMetadata.ToJsonString())
        };

        File.WriteAllText(Path.Combine(root, "ltx-start-image-contract.json"), summary.ToJsonString(JsonOptions));
    }

    private static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}
