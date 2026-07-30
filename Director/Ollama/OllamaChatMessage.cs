namespace Director.Ollama;

public sealed record OllamaChatMessage(string Role, string Content, IReadOnlyList<string>? Images = null);
