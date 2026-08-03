using Microsoft.Extensions.Options;

namespace Director.Options;

public sealed class OllamaOptionsValidator : IValidateOptions<OllamaOptions>
{
    public ValidateOptionsResult Validate(string? name, OllamaOptions options)
    {
        var failures = new List<string>();
        var configuredModels = new Dictionary<string, string>
        {
            [nameof(options.Model)] = options.Model,
            [nameof(options.StoryTextModel)] = options.StoryTextModel,
            [nameof(options.SceneTextModel)] = options.SceneTextModel,
            [nameof(options.PromptPreparationModel)] = options.PromptPreparationModel,
            [nameof(options.DialogueModel)] = options.DialogueModel,
            [nameof(options.VisualPromptModel)] = options.VisualPromptModel,
            [nameof(options.VideoPromptModel)] = options.VideoPromptModel
        };

        failures.AddRange(configuredModels
            .Where(item => !string.Equals(item.Value, OllamaOptions.DefaultTextModel, StringComparison.OrdinalIgnoreCase))
            .Select(item => $"{item.Key}={item.Value}; beklenen={OllamaOptions.DefaultTextModel}"));

        ValidateRange(failures, nameof(options.ContextLength), options.ContextLength, 4096, 131072);
        ValidateRange(failures, nameof(options.SceneNumPredict), options.SceneNumPredict, 256, 32768);
        ValidateRange(failures, nameof(options.SceneRepairNumPredict), options.SceneRepairNumPredict, 256, 32768);
        ValidateRange(failures, nameof(options.RequestTimeoutMinutes), options.RequestTimeoutMinutes, 1, 240);
        ValidateRange(failures, nameof(options.SceneConnectTimeoutSeconds), options.SceneConnectTimeoutSeconds, 1, 300);
        ValidateRange(failures, nameof(options.SceneFirstTokenTimeoutSeconds), options.SceneFirstTokenTimeoutSeconds, 1, 1800);
        ValidateRange(failures, nameof(options.SceneNoActivityTimeoutSeconds), options.SceneNoActivityTimeoutSeconds, 1, 600);
        ValidateRange(failures, nameof(options.SceneHardTimeoutMinutes), options.SceneHardTimeoutMinutes, 1, 240);
        ValidateRange(failures, nameof(options.MaxStructuredResponseCharacters), options.MaxStructuredResponseCharacters, 8192, 2_000_000);
        ValidateRange(failures, nameof(options.DiagnosticMaxRawResponseCharacters), options.DiagnosticMaxRawResponseCharacters, 1024, 1_000_000);
        ValidateRange(failures, nameof(options.DiagnosticRetentionMaxFiles), options.DiagnosticRetentionMaxFiles, 1, 10_000);
        ValidateRange(failures, nameof(options.DiagnosticRetentionMaxAgeDays), options.DiagnosticRetentionMaxAgeDays, 1, 3650);

        if (options.SceneBatchSize != 1)
        {
            failures.Add($"{nameof(options.SceneBatchSize)}={options.SceneBatchSize}; tek-sahne checkpoint akisi icin 1 olmali.");
        }

        var largestOutputBudget = Math.Max(options.SceneNumPredict, options.SceneRepairNumPredict);
        if (options.ContextLength <= largestOutputBudget)
        {
            failures.Add($"{nameof(options.ContextLength)}={options.ContextLength}; num_predict degerlerinden buyuk olmali. MaxNumPredict={largestOutputBudget}");
        }

        if (options.ContextLength - largestOutputBudget < 1024)
        {
            failures.Add($"{nameof(options.ContextLength)}={options.ContextLength}; prompt icin en az 1024 token pay birakmali. MaxNumPredict={largestOutputBudget}");
        }

        if (!IsValidKeepAlive(options.KeepAlive))
        {
            failures.Add($"{nameof(options.KeepAlive)}={options.KeepAlive}; beklenen format pozitif sure + birimdir, ornek: 30m, 1h, 45s.");
        }

        if (failures.Count > 0)
        {
            return ValidateOptionsResult.Fail(failures);
        }

        return ValidateOptionsResult.Success;
    }

    private static void ValidateRange(List<string> failures, string optionName, int value, int minInclusive, int maxInclusive)
    {
        if (value < minInclusive || value > maxInclusive)
        {
            failures.Add($"{optionName}={value}; aralik={minInclusive}..{maxInclusive}");
        }
    }

    private static bool IsValidKeepAlive(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        var unit = trimmed[^1];
        if (unit is not ('s' or 'm' or 'h'))
        {
            return false;
        }

        return int.TryParse(trimmed[..^1], out var amount) && amount > 0;
    }
}
