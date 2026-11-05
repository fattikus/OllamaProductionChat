using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OllamaSharp;

namespace OllamaProductionChat;

public static class OllamaSetup
{
    public static IServiceCollection AddOllama(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OllamaOptions>().Bind(configuration.GetSection(OllamaOptions.SectionName));

        // No AddStandardResilienceHandler(): retries duplicate GPU work and its attempt timeout cuts long generations; LlmTimeoutPolicy owns limits.
        services.AddHttpClient(OllamaOptions.HttpClientName, (sp, http) =>
        {
            http.BaseAddress = sp.GetRequiredService<IOptions<OllamaOptions>>().Value.BaseUrl;
            http.Timeout = Timeout.InfiniteTimeSpan;
        })
        // On dispose, SocketsHttpHandler drains an unfinished response for up to 2s to reuse the connection; Ollama keeps generating meanwhile.
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { ResponseDrainTimeout = TimeSpan.Zero });

        services.AddTransient<IOllamaApiClient>(sp => new CancellableOllamaApiClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(OllamaOptions.HttpClientName),
            sp.GetRequiredService<IOptions<OllamaOptions>>().Value.Model));

        services.AddChatClient(sp => (IChatClient)sp.GetRequiredService<IOllamaApiClient>(), ServiceLifetime.Transient)
            .UseLogging();

        services.AddTransient<AIAgent>(sp => sp.GetRequiredService<IChatClient>().AsAIAgent(name: "ollama-agent"));

        services.AddSingleton<LlmTimeoutPolicy>();
        services.AddSingleton<ModelWarmup>();
        services.AddSingleton<InMemoryConversationStore>();
        services.AddSingleton<ChatStreamer>();
        services.AddTransient<VramEstimator>();

        return services;
    }

    public static ChatOptions CreateChatOptions(this OllamaOptions options) => new()
    {
        ModelId = options.Model,
        Reasoning = options.Think ? null : new ReasoningOptions { Effort = ReasoningEffort.None },
        AdditionalProperties = new() { ["keep_alive"] = options.KeepAlive },
    };
}
