using Bedrock.Framework.Infrastructure;
using Bedrock.Framework.Protocols.Http.Http1;
using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;

namespace Bedrock.Framework.Protocols
{
    public class Http1ResponseMessageReader : IMessageReader<ParseResult<HttpResponseMessage>>
    {
        // Question: Do we want to inject this? Make it a singleton? What is the philosophy of this library WRT dependency injection?
        private static Http1HeaderReader _headerReader = new Http1HeaderReader();
        private ReadOnlySpan<byte> NewLine => new byte[] { (byte)'\r', (byte)'\n' };

        private HttpResponseMessage _httpResponseMessage = new HttpResponseMessage();

        private State _state;

        public Http1ResponseMessageReader(HttpContent content)
        {
            _httpResponseMessage.Content = content;
        }

        public bool TryParseMessage(in ReadOnlySequence<byte> input, ref SequencePosition consumed, ref SequencePosition examined, out ParseResult<HttpResponseMessage> message)
        {
            message = default;

            switch (_state)
            {
                case State.StartLine:
                    var sequenceReader = new SequenceReader<byte>(input);

                    if (!sequenceReader.TryReadTo(out ReadOnlySpan<byte> version, (byte)' '))
                    {
                        return false;
                    }

                    if (!sequenceReader.TryReadTo(out ReadOnlySpan<byte> statusCodeText, (byte)' '))
                    {
                        return false;
                    }

                    if (!sequenceReader.TryReadTo(out ReadOnlySequence<byte> statusText, NewLine))
                    {
                        return false;
                    }

                    Utf8Parser.TryParse(statusCodeText, out int statusCode, out _);

                    _httpResponseMessage.StatusCode = (HttpStatusCode)statusCode;
                    var reasonPhrase = Encoding.ASCII.GetString(statusText.IsSingleSegment ? statusText.FirstSpan : statusText.ToArray());
                    _httpResponseMessage.ReasonPhrase = reasonPhrase;
                    _httpResponseMessage.Version = new Version(1, 1); // TODO: Check

                    _state = State.Headers;

                    consumed = sequenceReader.Position;
                    examined = consumed;

                    goto case State.Headers;

                case State.Headers:
                    while (true)
                    {
                        var remaining = input.Slice(consumed);

                        if (remaining.StartsWith(NewLine))
                        {
                            consumed = remaining.GetPosition(2);
                            examined = consumed;
                            message = new ParseResult<HttpResponseMessage>(_httpResponseMessage);
                            _state = State.Body;
                            break;
                        }

                        if (!_headerReader.TryParseMessage(remaining, ref consumed, ref examined, out var headerResult))
                        {
                            return false;
                        }

                        if (headerResult.TryGetError(out var error))
                        {
                            message = new ParseResult<HttpResponseMessage>(error);
                            return true;
                        }

                        var success = headerResult.TryGetValue(out var header);
                        Debug.Assert(success == true);

                        var key = Encoding.ASCII.GetString(header.Name);
                        var value = Encoding.ASCII.GetString(header.Value);

                        if (!_httpResponseMessage.Headers.TryAddWithoutValidation(key, value))
                        {
                            _httpResponseMessage.Content.Headers.TryAddWithoutValidation(key, value);
                        }
                    }

                    break;
                default:
                    break;
            }

            return _state == State.Body;
        }

        private enum State
        {
            StartLine,
            Headers,
            Body
        }
    }
}
