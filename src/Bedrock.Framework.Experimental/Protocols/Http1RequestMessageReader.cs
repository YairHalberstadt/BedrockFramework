using Bedrock.Framework.Infrastructure;
using Bedrock.Framework.Protocols.Http.Http1;
using System;
using System.Buffers;
using System.Diagnostics;
using System.Net.Http;
using System.Text;

namespace Bedrock.Framework.Protocols
{
    public class Http1RequestMessageReader : IMessageReader<ParseResult<HttpRequestMessage>>
    {
        // Question: Do we want to inject this? Make it a singleton? What is the philosophy of this library WRT dependency injection?
        private static Http1HeaderReader _headerReader = new Http1HeaderReader();
        private ReadOnlySpan<byte> NewLine => new byte[] { (byte)'\r', (byte)'\n' };

        private HttpRequestMessage _httpRequestMessage = new HttpRequestMessage();

        private State _state;

        public Http1RequestMessageReader(HttpContent content)
        {
            _httpRequestMessage.Content = content;
        }

        public bool TryParseMessage(in ReadOnlySequence<byte> input, ref SequencePosition consumed, ref SequencePosition examined, out ParseResult<HttpRequestMessage> message)
        {
            message = default;

            if (_state == State.StartLine)
            {
                var sequenceReader = new SequenceReader<byte>(input);
                if (!sequenceReader.TryReadTo(out ReadOnlySpan<byte> method, (byte)' '))
                {
                    return false;
                }

                if (!sequenceReader.TryReadTo(out ReadOnlySpan<byte> path, (byte)' '))
                {
                    return false;
                }

                if (!sequenceReader.TryReadTo(out ReadOnlySequence<byte> version, NewLine))
                {
                    return false;
                }

                _httpRequestMessage.Method = new HttpMethod(Encoding.ASCII.GetString(method));
                _httpRequestMessage.RequestUri = new Uri(Encoding.ASCII.GetString(path), UriKind.Relative);
                _httpRequestMessage.Version = new Version(1, 1);
                // Version = Encoding.ASCII.GetString(version.IsSingleSegment ? version.FirstSpan : version.ToArray());

                _state = State.Headers;

                consumed = sequenceReader.Position;
                examined = consumed;
            }
            else if (_state == State.Headers)
            {
                while (true)
                {
                    var remaining = input.Slice(consumed);
                    if (remaining.StartsWith(NewLine))
                    {
                        _state = State.Body;
                        consumed = remaining.GetPosition(2);
                        examined = consumed;
                        message = new ParseResult<HttpRequestMessage>(_httpRequestMessage);
                        break;
                    }

                    if (!_headerReader.TryParseMessage(remaining, ref consumed, ref examined, out var headerResult))
                    {
                        return false;
                    }

                    if (headerResult.TryGetError(out var error))
                    {
                        message = new ParseResult<HttpRequestMessage>(error);
                        return true;
                    }

                    var success = headerResult.TryGetValue(out var header);
                    Debug.Assert(success == true);

                    var key = Encoding.ASCII.GetString(header.Name);
                    var value = Encoding.ASCII.GetString(header.Value);

                    if (!_httpRequestMessage.Headers.TryAddWithoutValidation(key, value))
                    {
                        _httpRequestMessage.Content.Headers.TryAddWithoutValidation(key, value);
                    }
                }
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
