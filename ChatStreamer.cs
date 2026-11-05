using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;

namespace OllamaProductionChat;

public sealed record ChatEvent(
    string? Text = null,
    int? Chunks = null,
    double? TimeToFirstTokenMs = null,
    double? TotalMs = null,
    long? OutputTokens = null,
    string? Reason = null,
    string? Message = null,
    bool? Retryable = null);

/// <summary>Turns an LLM update stream into SSE events, enforcing staged time limits and saving what the user actually saw.</summary>
public sealed class ChatStreamer(InMemoryConversationStore store, LlmTimeoutPolicy timeouts, ILogger<ChatStreamer> logger)
{
    public async IAsyncEnumerable<SseItem<ChatEvent>> StreamAsync(
        string conversationId,
        string userMessage,
        Func<IReadOnlyList<ChatMessage>, CancellationToken, IAsyncEnumerable<IList<AIContent>>> generate,
        [EnumeratorCancellation] CancellationToken requestAborted)
    {
        var prompt = store.AppendUserAndGetPrompt(conversationId, userMessage);

        using var timer = timeouts.Start(requestAborted);
        var answer = new StringBuilder();
        var chunks = 0;
        long? outputTokens = null;
        Exception? failure = null;
        var saved = false;

        try
        {
            await using var updates = generate(prompt, timer.Token).GetAsyncEnumerator(timer.Token);

            while (true)
            {
                try
                {
                    if (!await updates.MoveNextAsync())
                        break;
                }
                catch (Exception) when (timer.Token.IsCancellationRequested)
                {
                    // Depending on where it lands, cancellation surfaces as OperationCanceledException, IOException or ObjectDisposedException.
                    break;
                }
                catch (Exception ex)
                {
                    failure = ex;
                    break;
                }

                timer.MarkProgress();

                foreach (var content in updates.Current)
                {
                    switch (content)
                    {
                        case TextContent { Text.Length: > 0 } text:
                            chunks++;
                            answer.Append(text.Text);
                            yield return new SseItem<ChatEvent>(new ChatEvent(Text: text.Text), "token");
                            break;
                        case TextReasoningContent { Text.Length: > 0 } reasoning:
                            yield return new SseItem<ChatEvent>(new ChatEvent(Text: reasoning.Text), "thinking");
                            break;
                        case UsageContent usage:
                            outputTokens = usage.Details.OutputTokenCount;
                            break;
                    }
                }
            }

            // OllamaSharp ends the stream quietly when its token fires instead of throwing, so ask the timer why we stopped.
            var stopReason = timer.StopReason;
            var status = stopReason == LlmStopReason.None && failure is null ? MessageStatus.Complete : MessageStatus.Interrupted;
            store.AppendAssistant(conversationId, answer.ToString(), status);
            saved = true;

            switch (stopReason)
            {
                case LlmStopReason.ClientDisconnected:
                    logger.LogInformation("Generation cancelled by client after {Chunks} chunks in {Seconds:F1}s", chunks, timer.Elapsed.TotalSeconds);
                    yield break;

                case LlmStopReason.None when failure is null:
                    logger.LogInformation("Generation complete: {Chunks} chunks, TTFT {Ttft:F0}ms, total {Total:F0}ms",
                        chunks, timer.TimeToFirstToken?.TotalMilliseconds, timer.Elapsed.TotalMilliseconds);
                    yield return new SseItem<ChatEvent>(new ChatEvent(
                        Chunks: chunks,
                        TimeToFirstTokenMs: timer.TimeToFirstToken?.TotalMilliseconds,
                        TotalMs: timer.Elapsed.TotalMilliseconds,
                        OutputTokens: outputTokens), "done");
                    break;

                case LlmStopReason.None:
                    logger.LogError(failure, "Generation failed after {Chunks} chunks", chunks);
                    yield return new SseItem<ChatEvent>(new ChatEvent(Reason: "error", Message: failure!.Message, Retryable: true), "error");
                    break;

                default:
                    logger.LogWarning("Generation stopped by time limit {Reason} after {Chunks} chunks in {Seconds:F1}s", stopReason, chunks, timer.Elapsed.TotalSeconds);
                    yield return new SseItem<ChatEvent>(new ChatEvent(Reason: stopReason.ToString(), Message: $"The model hit the {stopReason} limit.", Retryable: true), "error");
                    break;
            }
        }
        finally
        {
            // Reached when the SSE writer stops enumerating early (client gone mid-write): keep the partial answer.
            if (!saved)
            {
                store.AppendAssistant(conversationId, answer.ToString(), MessageStatus.Interrupted);
                logger.LogInformation("Generation cancelled by client after {Chunks} chunks in {Seconds:F1}s", chunks, timer.Elapsed.TotalSeconds);
            }
        }
    }
}
