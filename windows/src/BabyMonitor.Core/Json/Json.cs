using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BabyMonitor.Core.Json;

/// <summary>
/// A small, permissive JSON object model — the C# twin of core's <c>json/Json.kt</c>.
///
/// The protocol layer reads Xiaomi's untyped, inconsistent responses through lenient <c>Opt*</c>
/// accessors: a missing or wrong-typed field reads as its default and never throws. That lenience is
/// the point — Xiaomi's responses vary by region, account state and firmware, and a monitor that
/// dies on an unexpected field at 3am is worse than one that carries on with a default.
///
/// Keeping the same shape as the Kotlin model is deliberate: the port stays line-for-line
/// comparable with the implementation that is known to talk to the real gateway.
///
/// Values are held as string, long, double, bool, JsonObj, JsonArr or null.
/// </summary>
public sealed class JsonObj
{
    private readonly Dictionary<string, object?> _values;
    private readonly List<string> _order; // insertion order: the signed form depends on it

    public JsonObj()
    {
        _values = new Dictionary<string, object?>(StringComparer.Ordinal);
        _order = new List<string>();
    }

    internal JsonObj(Dictionary<string, object?> values, List<string> order)
    {
        _values = values;
        _order = order;
    }

    public JsonObj(string json)
        : this()
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            root = doc.RootElement.Clone();
        }
        catch (Exception e)
        {
            throw new JsonException($"not JSON: {e.Message}");
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("not a JSON object");
        }

        foreach (var property in root.EnumerateObject())
        {
            Put(property.Name, JsonConvert.FromElement(property.Value));
        }
    }

    public JsonObj Put(string key, object? value)
    {
        if (!_values.ContainsKey(key))
        {
            _order.Add(key);
        }

        _values[key] = JsonConvert.Normalize(value);
        return this;
    }

    public bool Has(string key) => _values.ContainsKey(key);

    public bool IsNull(string key) => !_values.TryGetValue(key, out var v) || v is null;

    public object? Opt(string key) => _values.GetValueOrDefault(key);

    public object Get(string key) =>
        _values.GetValueOrDefault(key) ?? throw new JsonException($"no value for '{key}'");

    public string GetString(string key) => JsonConvert.AsString(Get(key));

    /// <summary>Containers have no string form worth having — they read as the default.</summary>
    public string OptString(string key, string fallback = "")
    {
        var v = _values.GetValueOrDefault(key);
        return v switch
        {
            null => fallback,
            JsonObj => fallback,
            JsonArr => fallback,
            _ => JsonConvert.AsString(v),
        };
    }

    public int OptInt(string key, int fallback = 0)
    {
        var n = JsonConvert.AsNumber(_values.GetValueOrDefault(key));
        return n is null ? fallback : (int)n.Value;
    }

    public double OptDouble(string key, double fallback) =>
        JsonConvert.AsNumber(_values.GetValueOrDefault(key)) ?? fallback;

    public bool OptBoolean(string key, bool fallback = false) =>
        _values.GetValueOrDefault(key) switch
        {
            bool b => b,
            string s => bool.TryParse(s, out var parsed) ? parsed : fallback,
            _ => fallback,
        };

    public int GetInt(string key)
    {
        var n = JsonConvert.AsNumber(Get(key));
        return n is null ? throw new JsonException($"'{key}' is not a number") : (int)n.Value;
    }

    public JsonObj GetJsonObject(string key) =>
        _values.GetValueOrDefault(key) as JsonObj ?? throw new JsonException($"'{key}' is not an object");

    public JsonObj? OptJsonObject(string key) => _values.GetValueOrDefault(key) as JsonObj;

    public JsonArr? OptJsonArray(string key) => _values.GetValueOrDefault(key) as JsonArr;

    public override string ToString()
    {
        var out_ = new StringBuilder();
        JsonConvert.WriteObject(out_, _order, _values);
        return out_.ToString();
    }
}

public sealed class JsonArr
{
    private readonly List<object?> _values;

    public JsonArr() => _values = new List<object?>();

    internal JsonArr(List<object?> values) => _values = values;

    public JsonArr Put(object? value)
    {
        _values.Add(JsonConvert.Normalize(value));
        return this;
    }

    public int Length => _values.Count;

    public object? Opt(int index) => index >= 0 && index < _values.Count ? _values[index] : null;

    public JsonObj GetJsonObject(int index) =>
        Opt(index) as JsonObj ?? throw new JsonException($"[{index}] is not an object");

    public JsonObj? OptJsonObject(int index) => Opt(index) as JsonObj;

    public override string ToString()
    {
        var out_ = new StringBuilder();
        JsonConvert.WriteArray(out_, _values);
        return out_.ToString();
    }
}

public sealed class JsonException : Exception
{
    public JsonException(string message)
        : base(message)
    {
    }
}

internal static class JsonConvert
{
    internal static object? FromElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => element.GetString(),
        // The cast is load-bearing: without it the conditional's type is `double`, and every whole
        // number in a Xiaomi response would arrive as 2.0 rather than 2 — which is not what the other
        // implementations hold, and is not what a MiOT property value compares equal to.
        JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
        JsonValueKind.Object => ObjectFrom(element),
        JsonValueKind.Array => ArrayFrom(element),
        _ => null,
    };

    private static JsonObj ObjectFrom(JsonElement element)
    {
        var obj = new JsonObj();
        foreach (var property in element.EnumerateObject())
        {
            obj.Put(property.Name, FromElement(property.Value));
        }

        return obj;
    }

    private static JsonArr ArrayFrom(JsonElement element)
    {
        var arr = new JsonArr();
        foreach (var item in element.EnumerateArray())
        {
            arr.Put(FromElement(item));
        }

        return arr;
    }

    internal static object? Normalize(object? value) => value switch
    {
        int i => (long)i,
        short s => (long)s,
        byte b => (long)b,
        float f => (double)f,
        _ => value,
    };

    internal static string AsString(object value) => value switch
    {
        string s => s,
        double d => FormatDouble(d),
        bool b => b ? "true" : "false",
        long l => l.ToString(CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    internal static double? AsNumber(object? value) => value switch
    {
        long l => l,
        double d => d,
        bool b => b ? 1.0 : 0.0,
        string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
        _ => null,
    };

    /// <summary>A whole-number double reads back as a whole number, not "5.0".</summary>
    internal static string FormatDouble(double value) =>
        double.IsFinite(value) && value == Math.Truncate(value) && Math.Abs(value) < 9e18
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);

    internal static void WriteObject(StringBuilder out_, List<string> order, Dictionary<string, object?> values)
    {
        out_.Append('{');
        var first = true;
        foreach (var key in order)
        {
            if (!first)
            {
                out_.Append(',');
            }

            first = false;
            WriteString(out_, key);
            out_.Append(':');
            WriteValue(out_, values.GetValueOrDefault(key));
        }

        out_.Append('}');
    }

    internal static void WriteArray(StringBuilder out_, List<object?> values)
    {
        out_.Append('[');
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                out_.Append(',');
            }

            WriteValue(out_, values[i]);
        }

        out_.Append(']');
    }

    private static void WriteValue(StringBuilder out_, object? value)
    {
        switch (value)
        {
            case null:
                out_.Append("null");
                break;
            case string s:
                WriteString(out_, s);
                break;
            case bool b:
                out_.Append(b ? "true" : "false");
                break;
            case long l:
                out_.Append(l.ToString(CultureInfo.InvariantCulture));
                break;
            case double d:
                out_.Append(FormatDouble(d));
                break;
            case JsonObj or JsonArr:
                out_.Append(value.ToString());
                break;
            default:
                WriteString(out_, value.ToString() ?? string.Empty);
                break;
        }
    }

    private static void WriteString(StringBuilder out_, string s)
    {
        out_.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':
                    out_.Append("\\\"");
                    break;
                case '\\':
                    out_.Append("\\\\");
                    break;
                case '\n':
                    out_.Append("\\n");
                    break;
                case '\r':
                    out_.Append("\\r");
                    break;
                case '\t':
                    out_.Append("\\t");
                    break;
                default:
                    if (c < ' ')
                    {
                        out_.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        out_.Append(c);
                    }

                    break;
            }
        }

        out_.Append('"');
    }
}
