using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WorldSim.Core
{
    /// <summary>
    /// Minimal dependency-free JSON reader/writer. Objects become
    /// Dictionary&lt;string, object&gt;, arrays List&lt;object&gt;, numbers double.
    /// Kept in-house so the core assembly has no Unity or NuGet dependencies.
    /// </summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            var p = new Parser(text);
            p.SkipWhitespace();
            object value = p.ParseValue();
            p.SkipWhitespace();
            if (!p.AtEnd) throw p.Error("Unexpected trailing characters");
            return value;
        }

        public static string Serialize(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object value)
        {
            switch (value)
            {
                case null: sb.Append("null"); break;
                case string s: WriteString(sb, s); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case double d: WriteNumber(sb, d); break;
                case float f: WriteNumber(sb, f); break;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case IDictionary<string, object> dict:
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, kv.Key);
                        sb.Append(':');
                        Write(sb, kv.Value);
                    }
                    sb.Append('}');
                    break;
                case System.Collections.IEnumerable list:
                    sb.Append('[');
                    bool firstItem = true;
                    foreach (object item in list)
                    {
                        if (!firstItem) sb.Append(',');
                        firstItem = false;
                        Write(sb, item);
                    }
                    sb.Append(']');
                    break;
                default:
                    throw new ArgumentException($"Unsupported JSON value type {value.GetType()}");
            }
        }

        private static void WriteNumber(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) throw new ArgumentException("JSON cannot represent NaN or Infinity");
            sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;

            public Parser(string s) { _s = s; }

            public bool AtEnd => _i >= _s.Length;

            public FormatException Error(string message) => new FormatException($"JSON: {message} at position {_i}");

            public void SkipWhitespace()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
            }

            public object ParseValue()
            {
                if (AtEnd) throw Error("Unexpected end of input");
                char c = _s[_i];
                switch (c)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return ParseString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber();
                        throw Error($"Unexpected character '{c}'");
                }
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0) throw Error($"Expected '{word}'");
                _i += word.Length;
            }

            private Dictionary<string, object> ParseObject()
            {
                var result = new Dictionary<string, object>();
                _i++;
                SkipWhitespace();
                if (!AtEnd && _s[_i] == '}') { _i++; return result; }
                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd || _s[_i] != '"') throw Error("Expected property name");
                    string key = ParseString();
                    SkipWhitespace();
                    if (AtEnd || _s[_i] != ':') throw Error("Expected ':'");
                    _i++;
                    SkipWhitespace();
                    result[key] = ParseValue();
                    SkipWhitespace();
                    if (AtEnd) throw Error("Unterminated object");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == '}') { _i++; return result; }
                    throw Error("Expected ',' or '}'");
                }
            }

            private List<object> ParseArray()
            {
                var result = new List<object>();
                _i++;
                SkipWhitespace();
                if (!AtEnd && _s[_i] == ']') { _i++; return result; }
                while (true)
                {
                    SkipWhitespace();
                    result.Add(ParseValue());
                    SkipWhitespace();
                    if (AtEnd) throw Error("Unterminated array");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == ']') { _i++; return result; }
                    throw Error("Expected ',' or ']'");
                }
            }

            private string ParseString()
            {
                var sb = new StringBuilder();
                _i++;
                while (true)
                {
                    if (AtEnd) throw Error("Unterminated string");
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (AtEnd) throw Error("Unterminated escape");
                    char e = _s[_i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_i + 4 > _s.Length) throw Error("Bad unicode escape");
                            sb.Append((char)int.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            _i += 4;
                            break;
                        default: throw Error($"Bad escape '\\{e}'");
                    }
                }
            }

            private double ParseNumber()
            {
                int start = _i;
                if (_s[_i] == '-') _i++;
                while (_i < _s.Length && "0123456789.eE+-".IndexOf(_s[_i]) >= 0) _i++;
                string token = _s.Substring(start, _i - start);
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    throw Error($"Bad number '{token}'");
                return d;
            }
        }
    }

    /// <summary>Typed accessors over parsed JSON objects.</summary>
    public static class JsonObjectExtensions
    {
        public static string GetString(this IDictionary<string, object> o, string key, string fallback = null) =>
            o.TryGetValue(key, out object v) && v is string s ? s : fallback;

        public static double GetDouble(this IDictionary<string, object> o, string key, double fallback = 0) =>
            o.TryGetValue(key, out object v) && v is double d ? d : fallback;

        public static int GetInt(this IDictionary<string, object> o, string key, int fallback = 0) =>
            o.TryGetValue(key, out object v) && v is double d ? (int)Math.Round(d) : fallback;

        public static IDictionary<string, object> GetObject(this IDictionary<string, object> o, string key) =>
            o.TryGetValue(key, out object v) ? v as IDictionary<string, object> : null;

        public static IList<object> GetArray(this IDictionary<string, object> o, string key) =>
            o.TryGetValue(key, out object v) ? v as IList<object> : null;

        public static string RequireString(this IDictionary<string, object> o, string key, string context)
        {
            string s = o.GetString(key);
            if (string.IsNullOrEmpty(s)) throw new FormatException($"{context}: missing required string '{key}'");
            return s;
        }
    }
}
