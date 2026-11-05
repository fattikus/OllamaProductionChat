using System.Text.Json;
using Microsoft.Extensions.Options;
using OllamaSharp;

namespace OllamaProductionChat;

public sealed record VramEstimate(
    string Model,
    string? Quantization,
    double ParametersBillions,
    int Layers,
    int KvHeads,
    int HeadDim,
    int NumCtx,
    int Parallel,
    int KvCacheBits,
    double WeightsGiBFromFile,
    double WeightsGiBFromFormula,
    double KvCacheKiBPerToken,
    double KvCacheGiB,
    double OverheadGiB,
    double EstimatedTotalGiB,
    double? ActualLoadedGiB,
    string? ActualProcessor);

/// <summary>Rule-of-thumb "will it fit?" maths: weights + KV cache + runtime overhead, read from the model's own metadata.</summary>
public sealed class VramEstimator(IOllamaApiClient client, IOptionsMonitor<OllamaOptions> options, ModelWarmup warmup)
{
    private const double GiB = 1024d * 1024 * 1024;
    private const double OverheadGiB = 0.75;

    // Effective bits per weight including quantization scales; K-quants mix block types, so these are averages.
    private static readonly Dictionary<string, double> BitsPerWeight = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Q4_0"] = 4.5, ["Q4_K_S"] = 4.6, ["Q4_K_M"] = 4.85, ["Q5_K_M"] = 5.7,
        ["Q6_K"] = 6.6, ["Q8_0"] = 8.5, ["F16"] = 16, ["BF16"] = 16,
    };

    public async Task<VramEstimate> EstimateAsync(int? numCtx, int parallel, int kvCacheBits, CancellationToken ct)
    {
        var model = options.CurrentValue.Model;
        var show = await client.ShowModelAsync(model, ct);
        var local = (await client.ListLocalModelsAsync(ct)).First(m => m.Name == model);
        var status = await warmup.GetStatusAsync(ct);

        var arch = show.Info.Architecture ?? show.Details.Family;
        var layers = ReadInt(show.Info.ExtraInfo, $"{arch}.block_count");
        var kvHeads = ReadInt(show.Info.ExtraInfo, $"{arch}.attention.head_count_kv");
        var headDim = ReadInt(show.Info.ExtraInfo, $"{arch}.attention.key_length");
        var parameters = show.Info.ParameterCount ?? 0;
        var ctx = numCtx ?? status.ContextLength ?? 4096;

        var bpw = BitsPerWeight.GetValueOrDefault(show.Details.QuantizationLevel ?? "", 16);
        var weightsFormula = parameters * bpw / 8 / GiB;
        var weightsFile = local.Size / GiB;

        // K and V, per layer, per KV head, per head dimension, per token.
        var kvBytesPerToken = 2d * layers * kvHeads * headDim * (kvCacheBits / 8d);
        var kvGiB = kvBytesPerToken * ctx * parallel / GiB;

        return new VramEstimate(
            model,
            show.Details.QuantizationLevel,
            Math.Round(parameters / 1e9, 2),
            layers,
            kvHeads,
            headDim,
            ctx,
            parallel,
            kvCacheBits,
            Math.Round(weightsFile, 2),
            Math.Round(weightsFormula, 2),
            Math.Round(kvBytesPerToken / 1024, 1),
            Math.Round(kvGiB, 2),
            OverheadGiB,
            Math.Round(weightsFile + kvGiB + OverheadGiB, 2),
            status.SizeBytes is { } size ? Math.Round(size / GiB, 2) : null,
            status.Processor);
    }

    private static int ReadInt(IDictionary<string, object>? info, string key) =>
        info?.TryGetValue(key, out var value) == true && value is JsonElement { ValueKind: JsonValueKind.Number } element
            ? element.GetInt32()
            : 0;
}
