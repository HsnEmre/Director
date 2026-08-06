using Director.Options;
using Microsoft.Extensions.Options;

namespace Director.Tests;

public sealed class OllamaOptionsValidatorTests
{
    private readonly OllamaOptionsValidator _validator = new();

    [Fact]
    public void CurrentProductionValues_AreValid()
    {
        var result = _validator.Validate(null, ValidOptions());

        Assert.True(result.Succeeded);
    }

    [Theory]
    [MemberData(nameof(InvalidNumericOptions))]
    public void InvalidNumericOption_ReturnsOptionName(Action<OllamaOptions> mutate, string expectedOptionName)
    {
        var options = ValidOptions();
        mutate(options);

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(expectedOptionName, result.FailureMessage);
    }

    public static IEnumerable<object[]> InvalidNumericOptions()
    {
        yield return [new Action<OllamaOptions>(options => options.ContextLength = 0), nameof(OllamaOptions.ContextLength)];
        yield return [new Action<OllamaOptions>(options => options.ContextLength = 999999), nameof(OllamaOptions.ContextLength)];
        yield return [new Action<OllamaOptions>(options => options.SceneNumPredict = 0), nameof(OllamaOptions.SceneNumPredict)];
        yield return [new Action<OllamaOptions>(options => options.SceneNumPredict = 999999), nameof(OllamaOptions.SceneNumPredict)];
        yield return [new Action<OllamaOptions>(options => options.SceneFreshRetryNumPredict = 0), nameof(OllamaOptions.SceneFreshRetryNumPredict)];
        yield return [new Action<OllamaOptions>(options => options.SceneRepairNumPredict = -1), nameof(OllamaOptions.SceneRepairNumPredict)];
        yield return [new Action<OllamaOptions>(options => options.SceneStructuredTopK = 0), nameof(OllamaOptions.SceneStructuredTopK)];
        yield return [new Action<OllamaOptions>(options => options.SceneStructuredRepeatLastN = -1), nameof(OllamaOptions.SceneStructuredRepeatLastN)];
        yield return [new Action<OllamaOptions>(options => options.RepetitionGuardMinCharacters = 127), nameof(OllamaOptions.RepetitionGuardMinCharacters)];
        yield return [new Action<OllamaOptions>(options => options.RepetitionGuardMinBlockCharacters = 15), nameof(OllamaOptions.RepetitionGuardMinBlockCharacters)];
        yield return [new Action<OllamaOptions>(options => options.RepetitionGuardMaxBlockCharacters = 31), nameof(OllamaOptions.RepetitionGuardMaxBlockCharacters)];
        yield return [new Action<OllamaOptions>(options => options.RepetitionGuardMinConsecutiveRepeats = 1), nameof(OllamaOptions.RepetitionGuardMinConsecutiveRepeats)];
        yield return [new Action<OllamaOptions>(options => options.RequestTimeoutMinutes = 0), nameof(OllamaOptions.RequestTimeoutMinutes)];
        yield return [new Action<OllamaOptions>(options => options.SceneConnectTimeoutSeconds = 0), nameof(OllamaOptions.SceneConnectTimeoutSeconds)];
        yield return [new Action<OllamaOptions>(options => options.SceneFirstTokenTimeoutSeconds = 0), nameof(OllamaOptions.SceneFirstTokenTimeoutSeconds)];
        yield return [new Action<OllamaOptions>(options => options.SceneNoActivityTimeoutSeconds = 0), nameof(OllamaOptions.SceneNoActivityTimeoutSeconds)];
        yield return [new Action<OllamaOptions>(options => options.SceneHardTimeoutMinutes = 0), nameof(OllamaOptions.SceneHardTimeoutMinutes)];
        yield return [new Action<OllamaOptions>(options => options.SceneBatchSize = 2), nameof(OllamaOptions.SceneBatchSize)];
        yield return [new Action<OllamaOptions>(options => options.MaxStructuredResponseCharacters = 8191), nameof(OllamaOptions.MaxStructuredResponseCharacters)];
        yield return [new Action<OllamaOptions>(options => options.MaxStructuredResponseCharacters = 2_000_001), nameof(OllamaOptions.MaxStructuredResponseCharacters)];
        yield return [new Action<OllamaOptions>(options => options.DiagnosticMaxRawResponseCharacters = 1023), nameof(OllamaOptions.DiagnosticMaxRawResponseCharacters)];
        yield return [new Action<OllamaOptions>(options => options.DiagnosticMaxRawResponseCharacters = 1_000_001), nameof(OllamaOptions.DiagnosticMaxRawResponseCharacters)];
        yield return [new Action<OllamaOptions>(options => options.DiagnosticRetentionMaxFiles = 0), nameof(OllamaOptions.DiagnosticRetentionMaxFiles)];
        yield return [new Action<OllamaOptions>(options => options.DiagnosticRetentionMaxFiles = 10_001), nameof(OllamaOptions.DiagnosticRetentionMaxFiles)];
        yield return [new Action<OllamaOptions>(options => options.DiagnosticRetentionMaxAgeDays = 0), nameof(OllamaOptions.DiagnosticRetentionMaxAgeDays)];
        yield return [new Action<OllamaOptions>(options => options.DiagnosticRetentionMaxAgeDays = 3651), nameof(OllamaOptions.DiagnosticRetentionMaxAgeDays)];
    }

    [Theory]
    [InlineData("")]
    [InlineData("0m")]
    [InlineData("-1m")]
    [InlineData("30")]
    [InlineData("30minutes")]
    public void InvalidKeepAlive_ReturnsOptionName(string keepAlive)
    {
        var options = ValidOptions();
        options.KeepAlive = keepAlive;

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(nameof(OllamaOptions.KeepAlive), result.FailureMessage);
    }

    [Fact]
    public void NumPredictMustLeavePromptBudgetInsideContext()
    {
        var options = ValidOptions();
        options.ContextLength = 7000;
        options.SceneNumPredict = 6144;
        options.SceneFreshRetryNumPredict = 6144;
        options.SceneRepairNumPredict = 6144;

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(nameof(OllamaOptions.ContextLength), result.FailureMessage);
        Assert.Contains("1024", result.FailureMessage);
    }

    [Fact]
    public void InvalidModelStillReturnsModelName()
    {
        var options = ValidOptions();
        options.SceneTextModel = "qwen3:4b-instruct";

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(nameof(OllamaOptions.SceneTextModel), result.FailureMessage);
        Assert.Contains("qwen3:4b-instruct", result.FailureMessage);
    }

    private static OllamaOptions ValidOptions() => new()
    {
        Model = OllamaOptions.DefaultTextModel,
        StoryTextModel = OllamaOptions.DefaultTextModel,
        SceneTextModel = OllamaOptions.DefaultTextModel,
        PromptPreparationModel = OllamaOptions.DefaultTextModel,
        DialogueModel = OllamaOptions.DefaultTextModel,
        VisualPromptModel = OllamaOptions.DefaultTextModel,
        VideoPromptModel = OllamaOptions.DefaultTextModel,
        KeepAlive = "30m",
        RequestTimeoutMinutes = 30,
        SceneConnectTimeoutSeconds = 15,
        SceneFirstTokenTimeoutSeconds = 600,
        SceneNoActivityTimeoutSeconds = 120,
        SceneHardTimeoutMinutes = 45,
        ContextLength = 32768,
        SceneBatchSize = 1,
        SceneNumPredict = 3072,
        SceneFreshRetryNumPredict = 3072,
        SceneRepairNumPredict = 2048,
        SceneStructuredTemperature = 0.2,
        SceneStructuredTopP = 0.8,
        SceneStructuredTopK = 40,
        SceneStructuredRepeatPenalty = 1.15,
        SceneStructuredRepeatLastN = 2048,
        RepetitionGuardEnabled = true,
        RepetitionGuardMinCharacters = 600,
        RepetitionGuardMinBlockCharacters = 48,
        RepetitionGuardMaxBlockCharacters = 512,
        RepetitionGuardMinConsecutiveRepeats = 4
    };
}
