using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace OllamaProductionChat;

public sealed record ChatRequestBody(string Message);

public static class ChatEndpoints
{
    public static void MapAiEndpoints(this IEndpointRouteBuilder app)
    {
        var ai = app.MapGroup("/api/ai");

        ai.MapPost("/warm", (ModelWarmup warmup) =>
            TypedResults.Accepted("/api/ai/status", new { started = warmup.TryStartWarmup() }));

        ai.MapGet("/status", (ModelWarmup warmup, CancellationToken ct) => warmup.GetStatusAsync(ct));

        ai.MapPost("/unload", async (ModelWarmup warmup, CancellationToken ct) =>
        {
            await warmup.UnloadAsync(ct);
            return TypedResults.NoContent();
        });

        ai.MapGet("/fit", (VramEstimator estimator, int? numCtx, int? parallel, int? kvCacheBits, CancellationToken ct) =>
            estimator.EstimateAsync(numCtx, parallel ?? 1, kvCacheBits ?? 16, ct));
    }

    public static void MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/chat/{conversationId}", (
            string conversationId,
            ChatRequestBody body,
            IChatClient chatClient,
            IOptionsMonitor<OllamaOptions> options,
            ChatStreamer streamer,
            HttpContext http) =>
        {
            var chatOptions = options.CurrentValue.CreateChatOptions();

            return TypedResults.ServerSentEvents(streamer.StreamAsync(
                conversationId,
                body.Message,
                (messages, ct) => chatClient.GetStreamingResponseAsync(messages, chatOptions, ct).Select(u => u.Contents),
                http.RequestAborted));
        });

        app.MapPost("/api/agent-chat/{conversationId}", (
            string conversationId,
            ChatRequestBody body,
            AIAgent agent,
            IOptionsMonitor<OllamaOptions> options,
            ChatStreamer streamer,
            HttpContext http) =>
        {
            var runOptions = new ChatClientAgentRunOptions(options.CurrentValue.CreateChatOptions());

            // No AgentSession: the store owns history and sends the whole list each turn, exactly like the IChatClient path.
            return TypedResults.ServerSentEvents(streamer.StreamAsync(
                conversationId,
                body.Message,
                (messages, ct) => agent.RunStreamingAsync(messages, session: null, runOptions, ct).Select(u => u.Contents),
                http.RequestAborted));
        });

        app.MapGet("/api/chat/{conversationId}", (string conversationId, InMemoryConversationStore store) => store.Get(conversationId));

        app.MapDelete("/api/chat/{conversationId}", (string conversationId, InMemoryConversationStore store) =>
            store.Delete(conversationId) ? Results.NoContent() : Results.NotFound());
    }
}
