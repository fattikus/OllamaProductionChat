namespace OllamaProductionChat;

public sealed class OllamaOptions
{
    public const string SectionName = "Ollama";
    public const string HttpClientName = "ollama";

    // Not "localhost": on Windows .NET tries ::1 first and Ollama listens on IPv4 only, costing ~2s per new connection.
    public Uri BaseUrl { get; set; } = new("http://127.0.0.1:11434");
    public string Model { get; set; } = "qwen3:8b";

    /// <summary>Ollama duration string ("30m", "1h") or "-1" to never unload. Sent on every request so the model stays warm on our terms, not the server default.</summary>
    public string KeepAlive { get; set; } = "30m";

    public bool Think { get; set; }
    public string SystemPrompt { get; set; } = "You are a helpful assistant. Keep answers short.";
    public int MaxHistoryMessages { get; set; } = 20;

    public TimeSpan TimeToFirstToken { get; set; } = TimeSpan.FromSeconds(90);
    public TimeSpan IdleTokenTimeout { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromMinutes(5);
}
