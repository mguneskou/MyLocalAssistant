using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyLocalAssistant.Shared.Plugins;

/// <summary>JSON-RPC 2.0 request/response envelope used between server and plug-in.</summary>
public sealed class RpcRequest
{
    [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
    [JsonPropertyName("id")] public long? Id { get; set; }
    [JsonPropertyName("method")] public string Method { get; set; } = "";
    /// <summary>Method parameters as a JSON object (kept as raw text so plug-ins can route without re-deserialising).</summary>
    [JsonPropertyName("params")]
    [JsonConverter(typeof(RawJsonElementConverter))]
    public JsonElement? Params { get; set; }
}

public sealed class RpcResponse
{
    [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
    [JsonPropertyName("id")] public long? Id { get; set; }
    [JsonPropertyName("result")]
    [JsonConverter(typeof(RawJsonElementConverter))]
    public JsonElement? Result { get; set; }
    [JsonPropertyName("error")] public RpcError? Error { get; set; }
}

public sealed class RpcError
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("data")]
    [JsonConverter(typeof(RawJsonElementConverter))]
    public JsonElement? Data { get; set; }
}

internal sealed class RawJsonElementConverter : JsonConverter<JsonElement?>
{
    public override JsonElement? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        using var doc = JsonDocument.ParseValue(ref reader);
        return doc.RootElement.Clone();
    }
    public override void Write(Utf8JsonWriter writer, JsonElement? value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        value.Value.WriteTo(writer);
    }
}

/// <summary>
/// LSP-style "Content-Length: N\r\n\r\n&lt;json&gt;" framing over a duplex byte stream.
/// Both sides import this helper. Designed for plain stdio.
/// </summary>
public static class JsonRpcFraming
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Write a single JSON object as a framed message.</summary>
    public static async Task WriteFrameAsync(Stream output, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), Json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {json.Length}\r\n\r\n");
        await output.WriteAsync(header, ct).ConfigureAwait(false);
        await output.WriteAsync(json, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Read one framed message. Returns the raw JSON bytes (without the header).
    /// Returns <c>null</c> on clean EOF.</summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream input, CancellationToken ct)
    {
        // Read the header line-by-line until a blank line.
        var headerBuffer = new MemoryStream(256);
        int? contentLength = null;
        while (true)
        {
            var line = await ReadLineAsync(input, headerBuffer, ct).ConfigureAwait(false);
            if (line is null) return null; // EOF before any header
            if (line.Length == 0) break;   // end of headers
            const string Prefix = "Content-Length:";
            if (line.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line.AsSpan(Prefix.Length).Trim(), out var len))
            {
                contentLength = len;
            }
            // Other headers (Content-Type, etc.) are tolerated and ignored.
        }
        if (contentLength is null || contentLength.Value < 0 || contentLength.Value > 64 * 1024 * 1024)
            throw new InvalidDataException("Missing or invalid Content-Length header.");
        var body = new byte[contentLength.Value];
        var read = 0;
        while (read < body.Length)
        {
            var n = await input.ReadAsync(body.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("Unexpected EOF inside JSON-RPC payload.");
            read += n;
        }
        return body;
    }

    private static async Task<string?> ReadLineAsync(Stream input, MemoryStream scratch, CancellationToken ct)
    {
        scratch.SetLength(0);
        var oneByte = new byte[1];
        var sawAny = false;
        while (true)
        {
            var n = await input.ReadAsync(oneByte.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0) return sawAny ? Encoding.ASCII.GetString(scratch.ToArray()) : null;
            sawAny = true;
            if (oneByte[0] == (byte)'\n')
            {
                var bytes = scratch.ToArray();
                // Trim trailing \r if any.
                var len = bytes.Length;
                if (len > 0 && bytes[len - 1] == (byte)'\r') len--;
                return Encoding.ASCII.GetString(bytes, 0, len);
            }
            scratch.WriteByte(oneByte[0]);
            if (scratch.Length > 8192) throw new InvalidDataException("JSON-RPC header line too long.");
        }
    }
}
