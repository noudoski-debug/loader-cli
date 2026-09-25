using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Loader
{
    /// <summary>
    /// Минимальный JSON-парсер/писер (без внешних зависимостей).
    /// Используется всегда — чтобы лоадер собирался чистым MSBuild без NuGet.
    /// </summary>
    public static class Json
    {
        // ============================ ПАРСЕР ============================

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;

            public Parser(string s) { _s = s ?? ""; }

            public object Parse()
            {
                SkipWs();
                object v = ReadValue();
                SkipWs();
                return v;
            }

            private void SkipWs()
            {
                while (_i < _s.Length && (char.IsWhiteSpace(_s[_i]) || _s[_i] == '\ufeff')) _i++;
            }

            private char Cur => _i < _s.Length ? _s[_i] : '\0';

            private void Expect(char c)
            {
                if (Cur != c) throw new FormatException("JSON: ожидался '" + c + "' на позиции " + _i);
                _i++;
            }

            private object ReadValue()
            {
                SkipWs();
                char c = Cur;
                switch (c)
                {
                    case '{': return ReadObject();
                    case '[': return ReadArray();
                    case '"': return ReadString();
                    case 't': ReadLiteral("true"); return true;
                    case 'f': ReadLiteral("false"); return false;
                    case 'n': ReadLiteral("null"); return null;
                    default: return ReadNumber();
                }
            }

            private void ReadLiteral(string word)
            {
                if (_i + word.Length > _s.Length ||
                    _s.Substring(_i, word.Length) != word)
                    throw new FormatException("JSON: битое значение на позиции " + _i);
                _i += word.Length;
            }

            private Dictionary<string, object> ReadObject()
            {
                var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                Expect('{');
                SkipWs();
                if (Cur == '}') { _i++; return d; }
                while (true)
                {
                    SkipWs();
                    string key = (string)ReadString();
                    SkipWs();
                    Expect(':');
                    d[key] = ReadValue();
                    SkipWs();
                    if (Cur == ',') { _i++; continue; }
                    Expect('}');
                    break;
                }
                return d;
            }

            private List<object> ReadArray()
            {
                var list = new List<object>();
                Expect('[');
                SkipWs();
                if (Cur == ']') { _i++; return list; }
                while (true)
                {
                    list.Add(ReadValue());
                    SkipWs();
                    if (Cur == ',') { _i++; continue; }
                    Expect(']');
                    break;
                }
                return list;
            }

            private string ReadString()
            {
                Expect('"');
                var sb = new StringBuilder();
                while (true)
                {
                    if (_i >= _s.Length) throw new FormatException("JSON: незакрытая строка");
                    char c = _s[_i++];
                    if (c == '"') break;
                    if (c == '\\')
                    {
                        char e = _i < _s.Length ? _s[_i++] : '"';
                        switch (e)
                        {
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case '/': sb.Append('/'); break;
                            case '\\': sb.Append('\\'); break;
                            case '"': sb.Append('"'); break;
                            case 'u':
                                if (_i + 4 > _s.Length) throw new FormatException("JSON: битый \\u");
                                sb.Append((char)int.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber));
                                _i += 4;
                                break;
                            default: sb.Append(e); break;
                        }
                    }
                    else sb.Append(c);
                }
                return sb.ToString();
            }

            private object ReadNumber()
            {
                int start = _i;
                while (_i < _s.Length && "+-.eE0123456789".IndexOf(_s[_i]) >= 0) _i++;
                string t = _s.Substring(start, _i - start);
                double d;
                if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    throw new FormatException("JSON: число '" + t + "' не разобрано");
                if (d == Math.Floor(d) && Math.Abs(d) < 1e15) return (long)d;
                return d;
            }
        }

        public static object Parse(string text)
        {
            return new Parser(text).Parse();
        }

        // ====================== ХЕЛПЕРЫ ДОСТУПА ======================

        public static Dictionary<string, object> Obj(object o)
        {
            return o as Dictionary<string, object>;
        }

        public static List<object> Arr(object o)
        {
            return o as List<object>;
        }

        public static string Str(Dictionary<string, object> d, string key, string def = "")
        {
            if (d == null) return def;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return def;
            return v is string ? (string)v : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static long Num(Dictionary<string, object> d, string key, long def = 0)
        {
            if (d == null) return def;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return def;
            if (v is long) return (long)v;
            if (v is double) return (long)(double)v;
            long r;
            return long.TryParse(Convert.ToString(v), out r) ? r : def;
        }

        public static bool Bool(Dictionary<string, object> d, string key, bool def = false)
        {
            if (d == null) return def;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return def;
            if (v is bool) return (bool)v;
            string s = v as string;
            if (s != null)
            {
                bool b;
                if (bool.TryParse(s, out b)) return b;
            }
            return def;
        }

        public static List<object> ArrOf(Dictionary<string, object> d, string key)
        {
            if (d == null) return new List<object>();
            object v;
            if (!d.TryGetValue(key, out v)) return new List<object>();
            var l = v as List<object>;
            return l ?? new List<object>();
        }

        // ============================ ПИСЕР ============================

        public static string Write(object value, bool indented = true)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, indented, 0);
            return sb.ToString();
        }

        private static void WriteIndent(StringBuilder sb, int level)
        {
            for (int i = 0; i < level; i++) sb.Append("  ");
        }

        private static void WriteValue(StringBuilder sb, object v, bool pretty, int level)
        {
            if (v == null) { sb.Append("null"); return; }

            var dict = v as Dictionary<string, object>;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    if (pretty) { sb.Append('\n'); WriteIndent(sb, level + 1); }
                    WriteString(sb, kv.Key);
                    sb.Append(':');
                    if (pretty) sb.Append(' ');
                    WriteValue(sb, kv.Value, pretty, level + 1);
                }
                if (pretty && !first) { sb.Append('\n'); WriteIndent(sb, level); }
                sb.Append('}');
                return;
            }

            var list = v as List<object>;
            if (list != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    if (pretty) { sb.Append('\n'); WriteIndent(sb, level + 1); }
                    WriteValue(sb, item, pretty, level + 1);
                }
                if (pretty && !first) { sb.Append('\n'); WriteIndent(sb, level); }
                sb.Append(']');
                return;
            }

            if (v is string) { WriteString(sb, (string)v); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is double)
            {
                sb.Append(((double)v).ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
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
    }
}
