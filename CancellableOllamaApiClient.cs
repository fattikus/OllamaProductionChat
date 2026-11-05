using System.Net;
using OllamaSharp;
using OllamaSharp.Models;

namespace OllamaProductionChat;

/// <summary>
/// OllamaSharp 5.5.x reads streamed lines with ReadLineAsync() without a token and only checks cancellation between lines,
/// so a stream that stalls ignores both time limits and a closed browser tab. Disposing the response when the token fires
/// unblocks the pending read and closes the connection, which is what tells Ollama to stop generating.
/// </summary>
public sealed class CancellableOllamaApiClient(HttpClient client, string defaultModel) : OllamaApiClient(client, defaultModel)
{
    // A DelegatingHandler can't do this: HttpClient hands handlers a linked token that it disposes once the headers arrive.
    protected override async Task<HttpResponseMessage> SendToOllamaAsync(
        HttpRequestMessage requestMessage,
        OllamaRequest? ollamaRequest,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        var response = await base.SendToOllamaAsync(requestMessage, ollamaRequest, completionOption, cancellationToken);

        if (completionOption == HttpCompletionOption.ResponseHeadersRead && cancellationToken.CanBeCanceled)
        {
            var registration = cancellationToken.Register(static state => ((HttpResponseMessage)state!).Dispose(), response);
            response.Content = new RegistrationOwningContent(response.Content, registration);
        }

        return response;
    }

    private sealed class RegistrationOwningContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly CancellationTokenRegistration _registration;

        public RegistrationOwningContent(HttpContent inner, CancellationTokenRegistration registration)
        {
            _inner = inner;
            _registration = registration;
            foreach (var header in inner.Headers)
                Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => _inner.CopyToAsync(stream);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            _inner.CopyToAsync(stream, cancellationToken);

        protected override Task<Stream> CreateContentReadStreamAsync() => _inner.ReadAsStreamAsync();

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => _inner.ReadAsStreamAsync(cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _registration.Dispose();
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
