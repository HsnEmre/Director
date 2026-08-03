namespace Director.Tests;

public sealed class StoryGenerationSmokeArgumentTests
{
    [Fact]
    public void NoArgs_DefaultsToDryRunWithoutProject()
    {
        var result = StoryGenerationSmokeArguments.Parse([]);

        Assert.True(result.Success);
        Assert.False(result.ShowHelp);
        Assert.False(result.Options.Write);
        Assert.Null(result.Options.ProjectId);
        Assert.Equal(1, result.Options.MaxScenes);
    }

    [Fact]
    public void Help_DoesNotSelectWriteMode()
    {
        var result = StoryGenerationSmokeArguments.Parse(["--help"]);

        Assert.True(result.Success);
        Assert.True(result.ShowHelp);
        Assert.False(result.Options.Write);
    }

    [Fact]
    public void ProjectIdWithoutWrite_IsReadOnly()
    {
        var result = StoryGenerationSmokeArguments.Parse(["--project-id", "9"]);

        Assert.True(result.Success);
        Assert.False(result.Options.Write);
        Assert.Equal(9, result.Options.ProjectId);
    }

    [Fact]
    public void WriteWithoutProjectId_IsControlledFailure()
    {
        var result = StoryGenerationSmokeArguments.Parse(["--write"]);

        Assert.False(result.Success);
        Assert.Contains("--project-id", result.ErrorMessage);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    public void InvalidProjectId_IsControlledFailure(string value)
    {
        var result = StoryGenerationSmokeArguments.Parse(["--project-id", value]);

        Assert.False(result.Success);
        Assert.Contains("--project-id", result.ErrorMessage);
    }

    [Fact]
    public void WriteWithProjectId_SelectsWriteMode()
    {
        var result = StoryGenerationSmokeArguments.Parse(["--write", "--project-id", "9"]);

        Assert.True(result.Success);
        Assert.True(result.Options.Write);
        Assert.Equal(9, result.Options.ProjectId);
        Assert.Equal(1, result.Options.MaxScenes);
    }

    [Fact]
    public void MultiSceneRequiresExplicitGuard()
    {
        var result = StoryGenerationSmokeArguments.Parse(["--write", "--project-id", "9", "--max-scenes", "2"]);

        Assert.False(result.Success);
        Assert.Contains("--allow-multiple-scenes", result.ErrorMessage);
    }

    [Fact]
    public void ExplicitMultiSceneGuard_AllowsMultipleScenes()
    {
        var result = StoryGenerationSmokeArguments.Parse(["--write", "--project-id", "9", "--max-scenes", "2", "--allow-multiple-scenes"]);

        Assert.True(result.Success);
        Assert.Equal(2, result.Options.MaxScenes);
    }

    [Fact]
    public void UnknownArgument_IsControlledFailure()
    {
        var result = StoryGenerationSmokeArguments.Parse(["--surprise"]);

        Assert.False(result.Success);
        Assert.Contains("Bilinmeyen", result.ErrorMessage);
    }

    [Fact]
    public void ParsingFailure_DoesNotProduceWritableExecutionPlan()
    {
        var result = StoryGenerationSmokeArguments.Parse(["--write", "--project-id", "0"]);

        Assert.False(result.Success);
        Assert.False(result.Options.Write);
        Assert.Null(result.Options.ProjectId);
    }
}
