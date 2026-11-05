# OllamaProductionChat

The companion sample for **"Production-Ready Ollama in .NET"**. It's a .NET 10 minimal API with a single vanilla-JS page that shows the plumbing a production Ollama chat needs and most demos skip:

| Concern | Where |
|---|---|
| Preload the model when the user opens the AI page, and set `keep_alive` | `ModelWarmup.cs`, `wwwroot/index.html`, `OllamaSetup.CreateChatOptions` |
| `HttpClient` from `IHttpClientFactory` with `Timeout = InfiniteTimeSpan` | `OllamaSetup.cs` |
| Separate time limits for first token, gaps between tokens, and total length | `LlmTimeoutPolicy.cs` |
| **No** retry or resilience handler on the LLM client | `OllamaSetup.cs` |
| Tokens streamed to the browser over SSE, through both `IChatClient` and Agent Framework | `ChatEndpoints.cs`, `ChatStreamer.cs` |
| `HttpContext.RequestAborted` passed all the way down, so closing the tab stops Ollama generating | `ChatEndpoints.cs`, `CancellableOllamaApiClient.cs` |
| The app keeps the chat history itself, so a conversation survives an Ollama restart | `InMemoryConversationStore.cs` |
| Will the model + context fit in VRAM? | `VramEstimator.cs` (`GET /api/ai/fit`) |

## Run it

Requirements: the .NET 10 SDK (pinned to 10.0.401 in `global.json`) and Ollama running with `qwen3:8b` pulled.

```powershell
dotnet run
# open http://localhost:5223
```

Configuration lives in `appsettings.json` under `Ollama`, and any setting can be overridden with an env var such as `Ollama__Model=llama3.2:3b`.

| Key | Default | Meaning |
|---|---|---|
| `BaseUrl` | `http://127.0.0.1:11434` | Don't use `localhost` (see gotchas) |
| `Model` | `qwen3:8b` | |
| `KeepAlive` | `30m` | Sent with every request. Ollama's own default is 5m. `-1` means keep forever |
| `WarmOnStartup` | `false` | Preload when the app starts, not when the page opens |
| `Think` | `false` | Maps to `ChatOptions.Reasoning.Effort = None`, which sends `think:false` |
| `TimeToFirstToken` | `00:01:30` | Covers a cold model load plus prompt eval |
| `IdleTokenTimeout` | `00:00:20` | Longest allowed gap between two chunks |
| `MaxDuration` | `00:05:00` | Hard cap for a whole answer |
| `MaxHistoryMessages` | `20` | How many turns are sent back to the model |

## Endpoints

| Method | Path | |
|---|---|---|
| POST | `/api/ai/warm` | Fire-and-forget preload. Returns 202 |
| GET | `/api/ai/status` | Loaded or warming, RAM/VRAM split, expiry (from `/api/ps`) |
| POST | `/api/ai/unload` | `keep_alive: 0`, for demoing a cold start |
| GET | `/api/ai/fit?numCtx=8192&parallel=1&kvCacheBits=16` | Estimates weights + KV cache and compares with the real footprint |
| POST | `/api/chat/{id}` | SSE stream via `IChatClient.GetStreamingResponseAsync` |
| POST | `/api/agent-chat/{id}` | SSE stream via Agent Framework `AIAgent.RunStreamingAsync` |
| GET / DELETE | `/api/chat/{id}` | History owned by the app |

The request body is `{"message":"..."}`. SSE events are `token`, `thinking`, `done` (chunks, TTFT, total, output tokens) and `error` (reason, message, retryable).

## Demo script

1. **Preload.** Run `ollama ps` and see it's empty. Open the page and the status pill moves from *warming* to *ready*. Run `ollama ps` again and the model is there, with an expiry 30 minutes out.
2. **Cold vs warm.** `POST /api/ai/unload`, then send a message. Now compare that with a message sent after the page has warmed the model (numbers below).
3. **Close the tab mid-answer.** Ask for a long essay, then close the tab (or click Stop). The app logs `Generation cancelled by client after N chunks in Xs`, and the runner's CPU drops to idle straight away. When `OLLAMA_NUM_PARALLEL=1` the next request starts immediately and doesn't queue behind a ghost generation.
4. **Ollama restart.** Have a conversation, then restart Ollama (`docker restart ollama`, or quit and relaunch the tray app). Send the next message and the conversation carries on. The app sends the whole history every turn, because Ollama keeps no sessions. Answers that were cut off are stored as `Interrupted`, and the UI shows a badge and a Retry button.
5. **Fit check.** `GET /api/ai/fit?numCtx=32768&parallel=2` shows the KV cache becoming the largest part of memory.

## Measured (my box)

The test machine runs Ollama 0.40.11 **CPU-only** (Intel Arc iGPU unused), with `qwen3:8b` Q4_K_M, `think:false`, prompt "Say hello in exactly five words.", and another app using the same Ollama at the same time.

| Scenario | TTFT | Total |
|---|---|---|
| Cold (model unloaded, first message) | 5.60 s / 5.27 s | 6.49 s / 6.12 s |
| Warm follow-up (same prompt prefix cached) | 0.18 s / 0.14 s | 1.16 s / 0.99 s |
| After page preload (`/api/ai/warm` took 4.6 s server-side) | 0.71 s / 0.69 s | 1.60 s / 1.57 s |

- The preload moves about 4.6 s of model load off the user's first message. The remaining ~0.5 s is prompt evaluation, because the warmup uses an empty prompt and so doesn't cache the system prompt.
- With `localhost`, every new connection took about 2.3 s. With `127.0.0.1` the first call took 0.24 s and pooled calls took 0.004 s.
- Abort: the client disconnected at 6 s and the generation stopped. The next request got its first token after 2.2 s. A control client that kept reading the essay made the next request wait 15.8 s.
- Each staged limit fired on time. Idle 3 s fired at 3.0 s, the 4 s TTFT limit at 4.3 s, and a 5 s MaxDuration at 5.3 s (the partial answer was saved as `Interrupted`).
- Fit for qwen3:8b at ctx 4096: weights 4.87 GiB, KV 0.56 GiB (144 KiB per token at F16), estimate 6.18 GiB. `/api/ps` reports 5.54 GiB. At ctx 32768 with 2 parallel slots the KV cache alone is 9 GiB.

## Gotchas this sample works around

1. **OllamaSharp ignores your token while it waits for the next line.** In 5.5.2 the streaming loop is `while (!ct.IsCancellationRequested) await reader.ReadLineAsync()`, and the token isn't passed to the read. A stalled stream therefore hangs even after the browser has gone. When cancellation does land between lines, the enumeration just ends: no `OperationCanceledException` is thrown. `CancellableOllamaApiClient` overrides `SendToOllamaAsync` so the token disposes the response, and `ChatStreamer` checks its own token to work out why the stream ended.
2. **A `DelegatingHandler` can't fix that.** `HttpClient` gives handlers a linked token that it disposes once the headers arrive.
3. **`SocketsHttpHandler.ResponseDrainTimeout` is 2 s by default.** When you dispose a cancelled response, .NET first tries to drain it, so Ollama keeps generating for up to 2 more seconds. The sample sets it to `TimeSpan.Zero`.
4. **On Windows, `localhost` adds about 2 s to every new connection.** .NET tries `::1` first, but Ollama only listens on `127.0.0.1`. After a cancel the connection is thrown away, so you pay this again on the next request.
5. **`keep_alive` is a string**, such as `"30m"` or `"-1"`, not a bool. Through `IChatClient` it goes in `ChatOptions.AdditionalProperties["keep_alive"]`.
6. **Don't add `AddStandardResilienceHandler()` to the LLM client.** Its 10 s attempt timeout and its retries cut off or duplicate expensive generations. Time limits are set per stage instead, and retrying is left to the user.
7. **Don't link the warmup to `RequestAborted`.** The page's warm call returns straight away, and the load should keep going after it does.
8. Use `HttpClient.Timeout = InfiniteTimeSpan`. The default 100 s kills long answers, and a single timeout can't tell "loading", "stalled" and "too long" apart.
