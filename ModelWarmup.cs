using System.Diagnostics;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;

namespace OllamaProductionChat;

public sealed record ModelStatus(
    string Model,
    bool Loaded,
    bool Warming,
    long? SizeBytes,
    long? SizeVramBytes,
    string? Processor,
    int? ContextLength,
    DateTime? ExpiresAt,
    double? LastWarmupSeconds,
    string? LastWarmupError);

/// <summary>Preloads the model on user intent (page open) so the VRAM load overlaps with the user typing.</summary>
public sealed class ModelWarmup(
    IServiceScopeFactory scopes,
    IOptionsMonitor<OllamaOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<ModelWarmup> logger)
{
    private readonly Lock _gate = new();
    private Task? _warming;
    private double? _lastWarmupSeconds;
    private string? _lastWarmupError;

    public bool IsWarming
    {
        get { lock (_gate) return _warming is { IsCompleted: false }; }
    }

    /// <summary>Starts a warmup unless one is already running; 50 open tabs cause one load call, not 50.</summary>
    public bool TryStartWarmup()
    {
        lock (_gate)
        {
            if (_warming is { IsCompleted: false })
                return false;

            // Not RequestAborted: the user navigating away mid-load shouldn't throw away a half-finished load.
            _warming = Task.Run(() => WarmAsync(lifetime.ApplicationStopping));
            return true;
        }
    }

    public async Task WarmAsync(CancellationToken cancellationToken)
    {
        var o = options.CurrentValue;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var scope = scopes.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<IOllamaApiClient>();

            // An empty prompt makes Ollama load the model and return immediately (done_reason "load") without generating.
            var request = new GenerateRequest { Model = o.Model, Prompt = "", Stream = false, KeepAlive = o.KeepAlive };
            await foreach (var _ in client.GenerateAsync(request, cancellationToken)) { }

            _lastWarmupSeconds = stopwatch.Elapsed.TotalSeconds;
            _lastWarmupError = null;
            logger.LogInformation("Model {Model} warm in {Seconds:F1}s (keep_alive {KeepAlive})", o.Model, _lastWarmupSeconds, o.KeepAlive);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastWarmupError = ex.Message;
            logger.LogWarning(ex, "Warmup of {Model} failed; the first chat message will pay the load cost", o.Model);
        }
    }

    public async Task UnloadAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IOllamaApiClient>().RequestModelUnloadAsync(options.CurrentValue.Model, cancellationToken);
    }

    public async Task<ModelStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var model = options.CurrentValue.Model;
        using var scope = scopes.CreateScope();
        var running = await scope.ServiceProvider.GetRequiredService<IOllamaApiClient>().ListRunningModelsAsync(cancellationToken);
        var match = running.FirstOrDefault(m => m.Name == model);

        return new ModelStatus(
            model,
            match is not null,
            IsWarming,
            match?.Size,
            match?.SizeVram,
            match is null ? null : DescribeProcessor(match.Size, match.SizeVram),
            match?.ContextLength,
            match?.ExpiresAt,
            _lastWarmupSeconds,
            _lastWarmupError);
    }

    // Same maths as the PROCESSOR column of `ollama ps`.
    private static string DescribeProcessor(long size, long sizeVram)
    {
        if (size <= 0 || sizeVram <= 0)
            return "100% CPU";
        if (sizeVram >= size)
            return "100% GPU";

        var cpu = (int)Math.Round((size - sizeVram) * 100.0 / size);
        return $"{cpu}%/{100 - cpu}% CPU/GPU";
    }
}
