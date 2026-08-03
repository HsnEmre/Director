using System.Text.Json;
using Director.Ollama;

namespace Director.Tests;

public sealed class OllamaStructuredJsonParserTests
{
    private static readonly OllamaStructuredJsonParser Parser = new();
    private static readonly OllamaResponseMetadata Metadata = new() { Model = "qwen3-vl:30b-a3b-instruct", Done = true, StreamCompleted = true };

    [Theory]
    [InlineData("{\"value\":\"ok\"}")]
    [InlineData("```json\n{\"value\":\"ok\"}\n```")]
    [InlineData("Here is the result:\n{\"value\":\"ok\"}")]
    [InlineData("{\"value\":\"ok\"}\nCompleted successfully.")]
    [InlineData("\uFEFF  {\"value\":\"ok\"}")]
    public void Parse_ExtractsFirstCompleteObject(string raw)
    {
        var result = Parser.Parse<SimpleResponse>(raw, Metadata);

        Assert.Equal("ok", result.Value.Value);
    }

    [Fact]
    public void Parse_PreservesEscapedQuotesBracesAndNestedValues()
    {
        const string raw = "prefix {\"value\":\"He said \\\"{go}\\\"\",\"nested\":{\"items\":[{\"id\":1}]}} suffix";

        var result = Parser.Parse<NestedResponse>(raw, Metadata);

        Assert.Equal("He said \"{go}\"", result.Value.Value);
        Assert.Equal(1, result.Value.Nested.Items[0].Id);
    }

    [Fact]
    public void Parse_IsPropertyNameCaseInsensitive()
    {
        var result = Parser.Parse<SimpleResponse>("{\"VALUE\":\"ok\",\"unknown\":true}", Metadata);

        Assert.Equal("ok", result.Value.Value);
    }

    [Theory]
    [InlineData("{\"characterKey\":\"metehan\",\"text\":\"Git {ve} \\\"kazan\\\"\"}")]
    [InlineData("[{\"characterKey\":\"metehan\",\"text\":\"Git {ve} \\\"kazan\\\"\"}]")]
    public void Parse_CanonicalizesNativeDialogueJsonObjectOrArray(string dialogueJson)
    {
        var raw = JsonSerializer.Serialize(new { dialogueJson = JsonDocument.Parse(dialogueJson).RootElement });

        var result = Parser.Parse<DialogueContainer>(raw, Metadata);
        using var canonical = JsonDocument.Parse(result.Value.DialogueJson);

        Assert.True(canonical.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array);
        Assert.Contains("Git {ve}", result.Value.DialogueJson);
    }

    [Fact]
    public void Parse_ValidatesDialogueJsonStoredInsideString()
    {
        const string raw = "{\"dialogueJson\":\"[{\\\"characterKey\\\":\\\"metehan\\\",\\\"text\\\":\\\"Merhaba\\\"}]\"}";

        var result = Parser.Parse<DialogueContainer>(raw, Metadata);

        Assert.Equal("[{\"characterKey\":\"metehan\",\"text\":\"Merhaba\"}]", result.Value.DialogueJson);
    }

    private sealed class SimpleResponse
    {
        public string Value { get; set; } = string.Empty;
    }

    private sealed class NestedResponse
    {
        public string Value { get; set; } = string.Empty;
        public NestedValue Nested { get; set; } = new();
    }

    private sealed class NestedValue
    {
        public List<NestedItem> Items { get; set; } = [];
    }

    private sealed class NestedItem
    {
        public int Id { get; set; }
    }

    private sealed class DialogueContainer
    {
        public string DialogueJson { get; set; } = string.Empty;
    }
}
