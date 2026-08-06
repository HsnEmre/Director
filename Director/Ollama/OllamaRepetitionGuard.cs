using System.Text;
using Director.Options;

namespace Director.Ollama;

internal sealed class OllamaRepetitionGuard
{
    private readonly OllamaOptions _options;

    public OllamaRepetitionGuard(OllamaOptions options)
    {
        _options = options;
    }

    public bool TryDetect(StringBuilder content, out OllamaRepetitionInfo repetition)
    {
        repetition = default;
        if (!_options.RepetitionGuardEnabled || content.Length < _options.RepetitionGuardMinCharacters)
        {
            return false;
        }

        var minBlockLength = Math.Max(16, _options.RepetitionGuardMinBlockCharacters);
        var maxBlockLength = Math.Min(
            Math.Max(minBlockLength, _options.RepetitionGuardMaxBlockCharacters),
            content.Length / Math.Max(2, _options.RepetitionGuardMinConsecutiveRepeats));
        var minRepeats = Math.Max(2, _options.RepetitionGuardMinConsecutiveRepeats);

        for (var blockLength = maxBlockLength; blockLength >= minBlockLength; blockLength--)
        {
            if (!TryCountSuffixRepeats(content, blockLength, minRepeats, out var repeatCount))
            {
                continue;
            }

            var blockStart = content.Length - blockLength;
            var block = content.ToString(blockStart, blockLength);
            if (!LooksLikeMeaningfulTextBlock(block))
            {
                continue;
            }

            repetition = new OllamaRepetitionInfo(blockLength, repeatCount, TrimPreview(block));
            return true;
        }

        return false;
    }

    private static bool TryCountSuffixRepeats(StringBuilder content, int blockLength, int minRepeats, out int repeatCount)
    {
        repeatCount = 1;
        var suffixStart = content.Length - blockLength;
        for (var candidateStart = suffixStart - blockLength; candidateStart >= 0; candidateStart -= blockLength)
        {
            if (!EqualsRange(content, candidateStart, suffixStart, blockLength))
            {
                break;
            }

            repeatCount++;
            if (repeatCount >= minRepeats)
            {
                return true;
            }
        }

        return false;
    }

    private static bool EqualsRange(StringBuilder content, int leftStart, int rightStart, int length)
    {
        for (var offset = 0; offset < length; offset++)
        {
            if (content[leftStart + offset] != content[rightStart + offset])
            {
                return false;
            }
        }

        return true;
    }

    private static bool LooksLikeMeaningfulTextBlock(string block)
    {
        if (block.Count(char.IsWhiteSpace) < 3)
        {
            return false;
        }

        if (block.Count(char.IsLetter) < 24)
        {
            return false;
        }

        return block.Contains(',', StringComparison.Ordinal) ||
               block.Contains(' ', StringComparison.Ordinal) ||
               block.Contains('-', StringComparison.Ordinal);
    }

    private static string TrimPreview(string block)
    {
        var normalized = string.Join(" ", block.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 160 ? normalized : normalized[..160];
    }
}

internal readonly record struct OllamaRepetitionInfo(int BlockLength, int RepeatCount, string Preview);
