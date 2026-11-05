using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace OllamaProductionChat;

public enum LlmStopReason
{
    None,
    ClientDisconnected,
    TimeToFirstToken,
    IdleBetweenTokens,
    MaxDuration,
}

/// <summary>Staged time limits for one LLM call: time to first token, idle gap between tokens, and a hard ceiling.</summary>
public sealed class LlmTimeoutPolicy(IOptionsMonitor<OllamaOptions> options)
{
    public LlmCallTimer Start(CancellationToken requestAborted)
    {
        var o = options.CurrentValue;
        return new LlmCallTimer(requestAborted, o.TimeToFirstToken, o.IdleTokenTimeout, o.MaxDuration);
    }
}

public sealed class LlmCallTimer : IDisposable
{
    private readonly CancellationToken _requestAborted;
    private readonly CancellationTokenSource _stage = new();
    private readonly CancellationTokenSource _total = new();
    private readonly CancellationTokenSource _linked;
    private readonly TimeSpan _idleTimeout;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public LlmCallTimer(CancellationToken requestAborted, TimeSpan timeToFirstToken, TimeSpan idleTimeout, TimeSpan maxDuration)
    {
        _requestAborted = requestAborted;
        _idleTimeout = idleTimeout;
        _stage.CancelAfter(timeToFirstToken);
        _total.CancelAfter(maxDuration);
        _linked = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, _stage.Token, _total.Token);
    }

    public CancellationToken Token => _linked.Token;
    public TimeSpan Elapsed => _stopwatch.Elapsed;
    public TimeSpan? TimeToFirstToken { get; private set; }

    public void MarkProgress()
    {
        TimeToFirstToken ??= _stopwatch.Elapsed;
        // CancelAfter reschedules the existing timer, so each chunk pushes the idle deadline forward.
        _stage.CancelAfter(_idleTimeout);
    }

    public LlmStopReason StopReason =>
        _requestAborted.IsCancellationRequested ? LlmStopReason.ClientDisconnected
        : _total.IsCancellationRequested ? LlmStopReason.MaxDuration
        : _stage.IsCancellationRequested ? (TimeToFirstToken is null ? LlmStopReason.TimeToFirstToken : LlmStopReason.IdleBetweenTokens)
        : LlmStopReason.None;

    public void Dispose()
    {
        _linked.Dispose();
        _stage.Dispose();
        _total.Dispose();
    }
}
