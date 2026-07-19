using System;
using System.Collections.Generic;
using System.Text;

namespace ZyweFrakcje
{
    /// <summary>
    /// Minimalny JSON writer/reader. Mod ModAPI ma białą listę assembly (zero Newtonsoft.Json,
    /// zero System.Net) — dla płaskich wiadomości mostka (docs/protocol.md) własny parser
    /// wystarcza i nie wymaga niczego spoza System/System.Collections/System.Text.
    /// </summary>
    internal static class Json
    {
        public sealed class Builder
        {
            private readonly StringBuilder _sb = new StringBuilder();
            private bool _first = true;

            public Builder()
            {
                _sb.Append('{');
            }

            public Builder Add(string key, string value)
            {
                Prefix(key);
                if (value == null)
                {
                    _sb.Append("null");
                }
                else
                {
                    _sb.Append('"').Append(Escape(value)).Append('"');
                }
                return this;
            }

            public Builder Add(string key, long value)
            {
                Prefix(key);
                _sb.Append(value);
                return this;
            }

            public Builder Add(string key, double value)
            {
                Prefix(key);
                _sb.Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return this;
            }

            public Builder Add(string key, bool value)
            {
                Prefix(key);
                _sb.Append(value ? "true" : "false");
                return this;
            }

            public Builder AddRaw(string key, string rawJson)
            {
                Prefix(key);
                _sb.Append(rawJson);
                return this;
            }

            public Builder AddStringArray(string key, IEnumerable<string> values)
            {
                Prefix(key);
                _sb.Append('[');
                bool first = true;
                foreach (string v in values)
                {
                    if (!first)
                    {
                        _sb.Append(',');
                    }
                    first = false;
                    _sb.Append('"').Append(Escape(v)).Append('"');
                }
                _sb.Append(']');
                return this;
            }

            public string Build()
            {
                return _sb.ToString() + "}";
            }

            private void Prefix(string key)
            {
                if (!_first)
                {
                    _sb.Append(',');
                }
                _first = false;
                _sb.Append('"').Append(Escape(key)).Append("\":");
            }
        }

        public static string Escape(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
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
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Parsuje jedną linię JSON w obiekt: Dictionary&lt;string,object&gt; / List&lt;object&gt; /
        /// string / double / bool / null. Wystarcza dla płaskich wiadomości mostka — nie jest to
        /// pełny walidator RFC 8259.
        /// </summary>
        public static object Parse(string text)
        {
            int pos = 0;
            object result = ParseValue(text, ref pos);
            SkipWhitespace(text, ref pos);
            if (pos != text.Length)
            {
                throw new FormatException("nieoczekiwane dane po wartości JSON na pozycji " + pos);
            }
            return result;
        }

        /// <summary>Odwrotność Parse: serializuje z powrotem model (Dictionary/List/string/double/bool/null).</summary>
        public static string Stringify(object value)
        {
            if (value == null)
            {
                return "null";
            }
            if (value is bool)
            {
                return ((bool)value) ? "true" : "false";
            }
            if (value is string)
            {
                return "\"" + Escape((string)value) + "\"";
            }
            if (value is double)
            {
                return ((double)value).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            var dict = value as Dictionary<string, object>;
            if (dict != null)
            {
                var sb = new StringBuilder();
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> kv in dict)
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }
                    first = false;
                    sb.Append('"').Append(Escape(kv.Key)).Append("\":").Append(Stringify(kv.Value));
                }
                sb.Append('}');
                return sb.ToString();
            }

            var list = value as List<object>;
            if (list != null)
            {
                var sb = new StringBuilder();
                sb.Append('[');
                bool first = true;
                foreach (object item in list)
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }
                    first = false;
                    sb.Append(Stringify(item));
                }
                sb.Append(']');
                return sb.ToString();
            }

            throw new FormatException("nieobsługiwany typ do serializacji: " + value.GetType());
        }

        public static Dictionary<string, object> ParseObject(string text)
        {
            object value = Parse(text);
            var obj = value as Dictionary<string, object>;
            if (obj == null)
            {
                throw new FormatException("oczekiwano obiektu JSON");
            }
            return obj;
        }

        private static object ParseValue(string s, ref int pos)
        {
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length)
            {
                throw new FormatException("niespodziewany koniec JSON");
            }
            char c = s[pos];
            if (c == '{') return ParseObjectValue(s, ref pos);
            if (c == '[') return ParseArray(s, ref pos);
            if (c == '"') return ParseString(s, ref pos);
            if (c == 't' || c == 'f') return ParseBool(s, ref pos);
            if (c == 'n') return ParseNull(s, ref pos);
            return ParseNumber(s, ref pos);
        }

        private static Dictionary<string, object> ParseObjectValue(string s, ref int pos)
        {
            var result = new Dictionary<string, object>();
            Expect(s, ref pos, '{');
            SkipWhitespace(s, ref pos);
            if (Peek(s, pos) == '}')
            {
                pos++;
                return result;
            }
            while (true)
            {
                SkipWhitespace(s, ref pos);
                string key = ParseString(s, ref pos);
                SkipWhitespace(s, ref pos);
                Expect(s, ref pos, ':');
                object value = ParseValue(s, ref pos);
                result[key] = value;
                SkipWhitespace(s, ref pos);
                char next = Peek(s, pos);
                if (next == ',')
                {
                    pos++;
                    continue;
                }
                if (next == '}')
                {
                    pos++;
                    break;
                }
                throw new FormatException("oczekiwano ',' albo '}' na pozycji " + pos);
            }
            return result;
        }

        private static List<object> ParseArray(string s, ref int pos)
        {
            var result = new List<object>();
            Expect(s, ref pos, '[');
            SkipWhitespace(s, ref pos);
            if (Peek(s, pos) == ']')
            {
                pos++;
                return result;
            }
            while (true)
            {
                object value = ParseValue(s, ref pos);
                result.Add(value);
                SkipWhitespace(s, ref pos);
                char next = Peek(s, pos);
                if (next == ',')
                {
                    pos++;
                    continue;
                }
                if (next == ']')
                {
                    pos++;
                    break;
                }
                throw new FormatException("oczekiwano ',' albo ']' na pozycji " + pos);
            }
            return result;
        }

        private static string ParseString(string s, ref int pos)
        {
            Expect(s, ref pos, '"');
            var sb = new StringBuilder();
            while (true)
            {
                if (pos >= s.Length)
                {
                    throw new FormatException("niezamknięty string JSON");
                }
                char c = s[pos++];
                if (c == '"')
                {
                    break;
                }
                if (c == '\\')
                {
                    if (pos >= s.Length)
                    {
                        throw new FormatException("niekompletny escape JSON");
                    }
                    char esc = s[pos++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (pos + 4 > s.Length)
                            {
                                throw new FormatException("niekompletny \\u escape JSON");
                            }
                            int code = Convert.ToInt32(s.Substring(pos, 4), 16);
                            sb.Append((char)code);
                            pos += 4;
                            break;
                        default:
                            throw new FormatException("nieznany escape '\\" + esc + "'");
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static bool ParseBool(string s, ref int pos)
        {
            if (string.CompareOrdinal(s, pos, "true", 0, 4) == 0)
            {
                pos += 4;
                return true;
            }
            if (string.CompareOrdinal(s, pos, "false", 0, 5) == 0)
            {
                pos += 5;
                return false;
            }
            throw new FormatException("oczekiwano true/false na pozycji " + pos);
        }

        private static object ParseNull(string s, ref int pos)
        {
            if (string.CompareOrdinal(s, pos, "null", 0, 4) == 0)
            {
                pos += 4;
                return null;
            }
            throw new FormatException("oczekiwano null na pozycji " + pos);
        }

        private static object ParseNumber(string s, ref int pos)
        {
            int start = pos;
            while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '-' || s[pos] == '+' || s[pos] == '.' || s[pos] == 'e' || s[pos] == 'E'))
            {
                pos++;
            }
            if (pos == start)
            {
                throw new FormatException("oczekiwano liczby na pozycji " + pos);
            }
            return double.Parse(s.Substring(start, pos - start), System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void SkipWhitespace(string s, ref int pos)
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos]))
            {
                pos++;
            }
        }

        private static char Peek(string s, int pos)
        {
            return pos < s.Length ? s[pos] : '\0';
        }

        private static void Expect(string s, ref int pos, char c)
        {
            if (pos >= s.Length || s[pos] != c)
            {
                throw new FormatException("oczekiwano '" + c + "' na pozycji " + pos);
            }
            pos++;
        }
    }
}
