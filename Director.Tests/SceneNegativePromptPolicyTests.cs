using Director.Services;

namespace Director.Tests;

public sealed class SceneNegativePromptPolicyTests
{
    [Fact]
    public void ImageNegativePrompt_DeduplicatesCaseInsensitiveTermsAndPreservesOrder()
    {
        var prompt = SceneNegativePromptPolicy.SanitizeImage("Watermark, 3D animated look, watermark, 3D animated look, blurry");

        Assert.Contains("watermark", prompt);
        Assert.Contains("3D animated look", prompt);
        Assert.Equal(prompt.IndexOf("text", StringComparison.Ordinal), prompt.LastIndexOf("text", StringComparison.Ordinal));
        Assert.Equal(prompt.IndexOf("watermark", StringComparison.OrdinalIgnoreCase), prompt.LastIndexOf("watermark", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(prompt.IndexOf("3D animated look", StringComparison.OrdinalIgnoreCase), prompt.LastIndexOf("3D animated look", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VideoNegativePrompt_UsesCanonicalFallbackWhenModelValueIsEmpty()
    {
        var prompt = SceneNegativePromptPolicy.SanitizeVideo("");

        Assert.Contains("scene transition", prompt);
        Assert.Contains("face morphing", prompt);
        Assert.True(prompt.Length <= SceneNegativePromptPolicy.MaxPromptCharacters);
    }

    [Fact]
    public void NegativePrompt_EnforcesMaximumLength()
    {
        var longPrompt = string.Join(", ", Enumerable.Range(0, 100).Select(index => $"unique repeated term {index:00}"));

        var prompt = SceneNegativePromptPolicy.SanitizeImage(longPrompt);

        Assert.True(prompt.Length <= SceneNegativePromptPolicy.MaxPromptCharacters);
        Assert.DoesNotContain("unique repeated term 99", prompt);
    }
}
