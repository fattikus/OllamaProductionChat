using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace OllamaProductionChat;

public enum MessageStatus
{
    Complete,
    Interrupted,
}

public sealed record HistoryMessage(string Role, string Text, string Status, DateTimeOffset At);

/// <summary>
/// The app owns the conversation; Ollama is a stateless function that gets the full message list every turn.
/// Swap for Postgres jsonb or Redis in production: <see cref="AIJsonUtilities.DefaultOptions"/> round-trips <see cref="ChatMessage"/>.
/// </summary>
public sealed class InMemoryConversationStore(IOptionsMonitor<OllamaOptions> options)
{
    private const string StatusKey = "status";
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _conversations = new();

    public IReadOnlyList<ChatMessage> AppendUserAndGetPrompt(string conversationId, string text)
    {
        var messages = _conversations.GetOrAdd(conversationId, _ => []);
        lock (messages)
        {
            // A retry after a failed turn re-sends the same text; don't store it twice.
            var last = messages.Count > 0 ? messages[^1] : null;
            if (last is null || last.Role != ChatRole.User || last.Text != text)
                messages.Add(Stamp(new ChatMessage(ChatRole.User, text), MessageStatus.Complete));

            var o = options.CurrentValue;
            // Naive context budget: system prompt + last N messages, so history never silently overflows num_ctx.
            return [new ChatMessage(ChatRole.System, o.SystemPrompt), .. messages.TakeLast(o.MaxHistoryMessages)];
        }
    }

    public void AppendAssistant(string conversationId, string text, MessageStatus status)
    {
        var messages = _conversations.GetOrAdd(conversationId, _ => []);
        lock (messages)
        {
            messages.Add(Stamp(new ChatMessage(ChatRole.Assistant, text), status));
        }
    }

    public IReadOnlyList<HistoryMessage> Get(string conversationId)
    {
        if (!_conversations.TryGetValue(conversationId, out var messages))
            return [];

        lock (messages)
        {
            return messages
                .Select(m => new HistoryMessage(
                    m.Role.Value,
                    m.Text,
                    m.AdditionalProperties?[StatusKey]?.ToString() ?? nameof(MessageStatus.Complete),
                    m.CreatedAt ?? DateTimeOffset.MinValue))
                .ToList();
        }
    }

    public bool Delete(string conversationId) => _conversations.TryRemove(conversationId, out _);

    private static ChatMessage Stamp(ChatMessage message, MessageStatus status)
    {
        message.CreatedAt = DateTimeOffset.UtcNow;
        message.AdditionalProperties = new() { [StatusKey] = status.ToString() };
        return message;
    }
}
