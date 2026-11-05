using System.Text.Json.Serialization;
using OllamaProductionChat;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOllama(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Ollama:WarmOnStartup"))
    app.Lifetime.ApplicationStarted.Register(() => app.Services.GetRequiredService<ModelWarmup>().TryStartWarmup());

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapAiEndpoints();
app.MapChatEndpoints();

app.Run();
