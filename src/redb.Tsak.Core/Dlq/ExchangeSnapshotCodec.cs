using System.Text;
using System.Text.Json;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Tsak.Core.Dlq;

/// <summary>Serialized form of an exchange snapshot for durable DLQ storage.</summary>
public sealed record SerializedSnapshot(
    string BodyKind,      // bytes | string | json | none
    string? BodyType,     // CLR type name when BodyKind=json
    string? BodyData,     // base64 (bytes) / literal (string) / json
    string HeadersJson,
    string PropertiesJson,
    bool Replayable);     // false when the body could not round-trip

/// <summary>
/// Serializes an <see cref="IExchange"/> snapshot for durable storage and rehydrates it back into a
/// fresh exchange for replay. The rehydrated exchange carries no DI scope — the route tail resolves
/// redb/SQL lazily from the context provider on replay (fresh connections, cleaned up by
/// <c>ReleaseScopes</c>), so no scope needs to be reattached here.
/// <para>
/// <b>Body fidelity:</b> <c>byte[]</c> and <c>string</c> round-trip exactly; other bodies go through
/// System.Text.Json (best-effort — the exact CLR type is restored when its assembly is loadable,
/// otherwise the body comes back as a <see cref="JsonElement"/>). A body that cannot be serialized at
/// all is stored with <c>BodyKind=none</c> and <c>Replayable=false</c> — the entry stays visible but
/// is not durably replayable (§fork-2 limitation).
/// </para>
/// </summary>
public static class ExchangeSnapshotCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static SerializedSnapshot Serialize(IExchange snapshot)
    {
        var msg = snapshot.In;
        var (kind, type, data, replayable) = SerializeBody(msg.Body);
        var headers = ScalarMap(msg.Headers);
        var props = ScalarMap(snapshot.Properties, skipKey: RouteCheckpoint.PropertyKey);
        return new SerializedSnapshot(kind, type, data,
            JsonSerializer.Serialize(headers, JsonOptions),
            JsonSerializer.Serialize(props, JsonOptions),
            replayable);
    }

    /// <summary>Rebuilds a fresh exchange from stored data, ready for <c>IRouteContext.ReplayAsync</c>.</summary>
    public static IExchange Rehydrate(SerializedSnapshot s)
    {
        var body = DeserializeBody(s.BodyKind, s.BodyType, s.BodyData);
        var message = new Message(body);
        foreach (var (k, v) in Deserialize(s.HeadersJson))
            message.Headers[k] = v;

        var exchange = new Exchange(message);
        foreach (var (k, v) in Deserialize(s.PropertiesJson))
            exchange.Properties[k] = v;

        return exchange;
    }

    // ── body ─────────────────────────────────────────────────────────

    private static (string kind, string? type, string? data, bool replayable) SerializeBody(object? body)
    {
        switch (body)
        {
            case null:
                return ("none", null, null, true);
            case byte[] bytes:
                return ("bytes", null, Convert.ToBase64String(bytes), true);
            case string str:
                return ("string", null, str, true);
            default:
                try
                {
                    var json = JsonSerializer.Serialize(body, body.GetType(), JsonOptions);
                    return ("json", body.GetType().AssemblyQualifiedName, json, true);
                }
                catch
                {
                    // Streams, delegates, non-serializable graphs — keep the entry visible, mark not replayable.
                    return ("none", null, null, false);
                }
        }
    }

    private static object? DeserializeBody(string kind, string? type, string? data)
    {
        if (data is null) return null;
        return kind switch
        {
            "bytes" => Convert.FromBase64String(data),
            "string" => data,
            "json" => DeserializeJsonBody(type, data),
            _ => null
        };
    }

    private static object? DeserializeJsonBody(string? type, string json)
    {
        // Restore the exact CLR type when its assembly is loadable; otherwise hand back a JsonElement
        // (still usable if the tail treats the body as JSON).
        if (!string.IsNullOrEmpty(type) && Type.GetType(type, throwOnError: false) is { } clr)
        {
            try { return JsonSerializer.Deserialize(json, clr, JsonOptions); }
            catch { /* fall through */ }
        }
        return JsonSerializer.Deserialize<JsonElement>(json, JsonOptions);
    }

    // ── headers / properties as scalar maps ──────────────────────────

    /// <summary>Keeps only JSON-friendly scalar values; complex objects are stringified.</summary>
    private static Dictionary<string, object?> ScalarMap(IDictionary<string, object?> source, string? skipKey = null)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in source)
        {
            if (k == skipKey) continue;
            result[k] = v switch
            {
                null or string or bool or int or long or double or decimal => v,
                byte[] b => Convert.ToBase64String(b),
                _ => v.ToString()
            };
        }
        return result;
    }

    private static Dictionary<string, object?> Deserialize(string json)
    {
        if (string.IsNullOrEmpty(json)) return new();
        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOptions) ?? new();
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, e) in raw)
            result[k] = e.ValueKind switch
            {
                JsonValueKind.String => e.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
                JsonValueKind.Null => null,
                _ => e.GetRawText()
            };
        return result;
    }
}
