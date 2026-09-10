using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ComfyTray;

/// <summary>
/// Newline-delimited UTF-8 JSON framing for the guard protocol.
///
/// <para>
/// Byte-mode framing rather than a named pipe's message mode. Message mode looks tidier but
/// brings <c>ERROR_MORE_DATA</c> truncation handling with it, and — decisively — cannot be
/// exercised over a <see cref="MemoryStream"/>. Framing done this way is
/// <see cref="Stream"/>-generic, so the tests below drive exactly the code the service runs.
/// </para>
/// </summary>
internal static class MessageFraming
{
    /// <summary>
    /// Ceiling on a single message. The guard reads from a pipe reachable by any interactive
    /// user, and an unbounded read against a caller that never sends a newline is a
    /// memory-exhaustion bug waiting to happen.
    /// </summary>
    public const int MaxLineBytes = 64 * 1024;

    /// <summary>Writes one framed message and flushes it.</summary>
    public static void Write(Stream stream, string payload)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(payload);

        var bytes = Encoding.UTF8.GetBytes(payload);
        if (bytes.Length + 1 > MaxLineBytes)
        {
            throw new InvalidDataException(
                $"Message of {bytes.Length} bytes exceeds the {MaxLineBytes} byte frame limit.");
        }

        stream.Write(bytes, 0, bytes.Length);
        stream.WriteByte((byte)'\n');
        stream.Flush();
    }

    /// <summary>Serialises a request to its wire form.</summary>
    public static string Serialize(GuardRequest request) =>
        JsonSerializer.Serialize(request, GuardJsonContext.Default.GuardRequest);

    /// <summary>Serialises a response to its wire form.</summary>
    public static string Serialize(GuardResponse response) =>
        JsonSerializer.Serialize(response, GuardJsonContext.Default.GuardResponse);

    /// <summary>
    /// Parses a request. Throws <see cref="JsonException"/> on anything malformed or carrying an
    /// unrecognised discriminator, which the connection handler turns into an error response
    /// rather than a crash.
    /// </summary>
    public static GuardRequest DeserializeRequest(string payload) =>
        JsonSerializer.Deserialize(payload, GuardJsonContext.Default.GuardRequest)
        ?? throw new JsonException("Message deserialised to null.");

    /// <summary>Parses a response.</summary>
    public static GuardResponse DeserializeResponse(string payload) =>
        JsonSerializer.Deserialize(payload, GuardJsonContext.Default.GuardResponse)
        ?? throw new JsonException("Message deserialised to null.");
}

/// <summary>
/// Reads newline-delimited frames from a stream, buffering so that a message is not assembled a
/// byte at a time, and refusing anything past <see cref="MessageFraming.MaxLineBytes"/>.
/// </summary>
internal sealed class FrameReader(Stream stream)
{
    private readonly Stream _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private readonly byte[] _buffer = new byte[8192];
    private int _position;
    private int _length;

    /// <summary>
    /// Reads the next frame, or null at a clean end of stream.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The stream ended part-way through a frame, or a frame exceeded the size limit.
    /// </exception>
    public string? ReadLine()
    {
        using var assembled = new MemoryStream();

        while (true)
        {
            if (_position >= _length && !Fill())
            {
                // A clean end of stream between frames is the peer hanging up, which is normal
                // and means the session is over. Ending mid-frame is not.
                if (assembled.Length == 0)
                {
                    return null;
                }

                throw new InvalidDataException(
                    $"Stream ended {assembled.Length} bytes into an unterminated frame.");
            }

            var newline = Array.IndexOf(_buffer, (byte)'\n', _position, _length - _position);
            var take = newline >= 0 ? newline - _position : _length - _position;

            if (assembled.Length + take > MessageFraming.MaxLineBytes)
            {
                throw new InvalidDataException(
                    $"Frame exceeds the {MessageFraming.MaxLineBytes} byte limit.");
            }

            assembled.Write(_buffer, _position, take);
            _position += take;

            if (newline < 0)
            {
                continue;
            }

            _position++;
            return Decode(assembled);
        }
    }

    private bool Fill()
    {
        _length = _stream.Read(_buffer, 0, _buffer.Length);
        _position = 0;
        return _length > 0;
    }

    private static string Decode(MemoryStream assembled)
    {
        var length = (int)assembled.Length;

        // Tolerate CRLF, so a frame written by something line-oriented still parses.
        if (length > 0 && assembled.GetBuffer()[length - 1] == (byte)'\r')
        {
            length--;
        }

        return Encoding.UTF8.GetString(assembled.GetBuffer(), 0, length);
    }
}
