using System.Text.Json;
using System.Text.Json.Nodes;

namespace Director.Ollama;

public sealed class OllamaStructuredJsonParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public OllamaStructuredResult<T> Parse<T>(string rawResponse, OllamaResponseMetadata metadata)
    {
        string extracted;
        try
        {
            extracted = ExtractFirstCompleteObject(rawResponse);
        }
        catch (JsonException ex)
        {
            throw new OllamaStructuredResponseException(
                "Model yanitinda tamamlanmis bir JSON object bulunamadi.",
                "Normalization",
                rawResponse,
                metadata,
                ex);
        }

        string normalized;
        try
        {
            normalized = NormalizeEmbeddedJsonFields(extracted);
            using var _ = JsonDocument.Parse(normalized);
        }
        catch (JsonException ex)
        {
            throw new OllamaStructuredResponseException(
                "Model yaniti JSON syntax dogrulamasindan gecemedi.",
                "SyntaxValidation",
                rawResponse,
                metadata,
                ex);
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(normalized, JsonOptions)
                ?? throw new JsonException("DTO deserialization returned null.");
            return new OllamaStructuredResult<T>
            {
                Value = value,
                RawResponse = rawResponse,
                NormalizedJson = normalized,
                Metadata = metadata
            };
        }
        catch (JsonException ex)
        {
            throw new OllamaStructuredResponseException(
                "Model yaniti DTO'ya donusturulemedi.",
                "DtoDeserialization",
                rawResponse,
                metadata,
                ex);
        }
    }

    public static string ExtractFirstCompleteObject(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            throw new JsonException("Response is empty.");
        }

        var text = response.TrimStart('\uFEFF').Trim();
        for (var candidateStart = text.IndexOf('{'); candidateStart >= 0; candidateStart = text.IndexOf('{', candidateStart + 1))
        {
            if (!TryFindBalancedObjectEnd(text, candidateStart, out var end))
            {
                continue;
            }

            var candidate = text[candidateStart..(end + 1)];
            try
            {
                using var document = JsonDocument.Parse(candidate);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return candidate;
                }
            }
            catch (JsonException)
            {
                // Continue with the next opening brace; explanatory text may contain braces.
            }
        }

        throw new JsonException("No complete balanced top-level JSON object was found.");
    }

    private static bool TryFindBalancedObjectEnd(string text, int start, out int end)
    {
        var stack = new Stack<char>();
        var inString = false;
        var escaped = false;
        for (var index = start; index < text.Length; index++)
        {
            var current = text[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (current == '"')
            {
                inString = true;
                continue;
            }

            if (current is '{' or '[')
            {
                stack.Push(current);
                continue;
            }

            if (current is not ('}' or ']'))
            {
                continue;
            }

            if (stack.Count == 0 || !IsMatchingPair(stack.Pop(), current))
            {
                end = -1;
                return false;
            }

            if (stack.Count == 0)
            {
                end = index;
                return true;
            }
        }

        end = -1;
        return false;
    }

    private static bool IsMatchingPair(char opening, char closing) =>
        (opening == '{' && closing == '}') || (opening == '[' && closing == ']');

    private static string NormalizeEmbeddedJsonFields(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new JsonException("Top-level JSON must be an object.");
        var dialogueProperty = root.FirstOrDefault(item =>
            string.Equals(item.Key, "dialogueJson", StringComparison.OrdinalIgnoreCase));
        if (dialogueProperty.Key is null || dialogueProperty.Value is null)
        {
            return root.ToJsonString();
        }

        var canonical = dialogueProperty.Value switch
        {
            JsonArray or JsonObject => dialogueProperty.Value.ToJsonString(),
            JsonValue value when value.TryGetValue<string>(out var nestedJson) => CanonicalizeNestedJson(nestedJson),
            _ => throw new JsonException("dialogueJson must be a JSON string, object or array.")
        };
        root[dialogueProperty.Key] = canonical;
        return root.ToJsonString();
    }

    private static string CanonicalizeNestedJson(string nestedJson)
    {
        using var document = JsonDocument.Parse(nestedJson);
        if (document.RootElement.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
        {
            throw new JsonException("dialogueJson string must contain a JSON object or array.");
        }

        return document.RootElement.GetRawText();
    }
}
