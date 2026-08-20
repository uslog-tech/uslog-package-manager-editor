using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Uslog.PackageManager.Editor
{
    /// <summary>
    /// 最小限の JSON リーダ / ライタ。
    ///
    /// Unity の JsonUtility はキーが動的なオブジェクト（VPM リスティングの
    /// packages や manifest.json の dependencies）を扱えない。Newtonsoft を
    /// 足す手もあるが、VRChat SDK が別バージョンを固定していることがあり、
    /// クライアント側の都合で依存を増やすと入らないプロジェクトが出る。
    ///
    /// manifest.json のような「利用者のファイル」を書き戻すので、
    /// 知らないフィールドと**キーの順序**を保つことを最優先にしている。
    /// 数値は文字列のまま持ち、1 を 1.0 に化けさせない。
    /// </summary>
    public enum JsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object,
    }

    public class JsonException : Exception
    {
        public JsonException(string message) : base(message) { }
    }

    public sealed class JsonValue
    {
        private readonly List<string> _keys;
        private readonly Dictionary<string, JsonValue> _members;
        private readonly List<JsonValue> _items;
        private readonly string _raw;
        private readonly bool _bool;

        public JsonKind Kind { get; }

        private JsonValue(JsonKind kind, string raw = null, bool boolean = false)
        {
            Kind = kind;
            _raw = raw;
            _bool = boolean;

            if (kind == JsonKind.Object)
            {
                _keys = new List<string>();
                _members = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            }
            else if (kind == JsonKind.Array)
            {
                _items = new List<JsonValue>();
            }
        }

        // ------------------------------------------------------------ 生成

        public static JsonValue Null => new JsonValue(JsonKind.Null);
        public static JsonValue NewObject() => new JsonValue(JsonKind.Object);
        public static JsonValue NewArray() => new JsonValue(JsonKind.Array);

        public static JsonValue String(string value)
        {
            return value == null ? Null : new JsonValue(JsonKind.String, value);
        }

        public static JsonValue Bool(bool value) => new JsonValue(JsonKind.Bool, null, value);

        public static JsonValue Number(double value)
        {
            return new JsonValue(JsonKind.Number, value.ToString("R", CultureInfo.InvariantCulture));
        }

        public static JsonValue Number(long value)
        {
            return new JsonValue(JsonKind.Number, value.ToString(CultureInfo.InvariantCulture));
        }

        // ------------------------------------------------------------ 読み

        public bool IsNull => Kind == JsonKind.Null;
        public bool IsObject => Kind == JsonKind.Object;
        public bool IsArray => Kind == JsonKind.Array;
        public bool IsString => Kind == JsonKind.String;

        /// <summary>文字列でなければ null。呼び出し側で分岐を書かなくて済むように。</summary>
        public string AsString => Kind == JsonKind.String ? _raw : null;

        /// <summary>文字列 / 数値 / 真偽をそのまま表示用の文字列にする。</summary>
        public string AsText
        {
            get
            {
                switch (Kind)
                {
                    case JsonKind.String:
                    case JsonKind.Number:
                        return _raw;
                    case JsonKind.Bool:
                        return _bool ? "true" : "false";
                    default:
                        return null;
                }
            }
        }

        public bool AsBool => Kind == JsonKind.Bool && _bool;

        public double AsNumber
        {
            get
            {
                if (Kind != JsonKind.Number) return 0d;
                return double.TryParse(_raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    ? d
                    : 0d;
            }
        }

        public int Count
        {
            get
            {
                if (Kind == JsonKind.Array) return _items.Count;
                if (Kind == JsonKind.Object) return _keys.Count;
                return 0;
            }
        }

        /// <summary>キーが無くても null を返さない。連鎖して書けるようにするため。</summary>
        public JsonValue this[string key]
        {
            get
            {
                if (Kind != JsonKind.Object || key == null) return Null;
                return _members.TryGetValue(key, out var found) ? found : Null;
            }
        }

        public JsonValue this[int index]
        {
            get
            {
                if (Kind != JsonKind.Array || index < 0 || index >= _items.Count) return Null;
                return _items[index];
            }
        }

        public bool Has(string key)
        {
            return Kind == JsonKind.Object && key != null && _members.ContainsKey(key);
        }

        /// <summary>挿入順のキー。書き戻したときに並びが入れ替わらないように。</summary>
        public IReadOnlyList<string> Keys => Kind == JsonKind.Object ? _keys : (IReadOnlyList<string>)System.Array.Empty<string>();

        public IReadOnlyList<JsonValue> Items => Kind == JsonKind.Array ? _items : (IReadOnlyList<JsonValue>)System.Array.Empty<JsonValue>();

        // ------------------------------------------------------------ 書き

        public JsonValue Set(string key, JsonValue value)
        {
            if (Kind != JsonKind.Object) throw new JsonException("Set はオブジェクトにしか使えません");
            if (key == null) throw new ArgumentNullException(nameof(key));

            if (!_members.ContainsKey(key)) _keys.Add(key);
            _members[key] = value ?? Null;
            return this;
        }

        public JsonValue Set(string key, string value) => Set(key, String(value));
        public JsonValue Set(string key, bool value) => Set(key, Bool(value));

        public bool Remove(string key)
        {
            if (Kind != JsonKind.Object || key == null) return false;
            if (!_members.Remove(key)) return false;
            _keys.Remove(key);
            return true;
        }

        public JsonValue Add(JsonValue value)
        {
            if (Kind != JsonKind.Array) throw new JsonException("Add は配列にしか使えません");
            _items.Add(value ?? Null);
            return this;
        }

        // ------------------------------------------------------------ 解析

        public static JsonValue Parse(string text)
        {
            if (text == null) throw new JsonException("入力が null です");

            var parser = new Parser(text);
            parser.SkipWhitespace();
            var value = parser.ReadValue(0);
            parser.SkipWhitespace();
            if (!parser.AtEnd) throw new JsonException($"末尾に余分な文字があります (位置 {parser.Position})");
            return value;
        }

        public static bool TryParse(string text, out JsonValue value)
        {
            try
            {
                value = Parse(text);
                return true;
            }
            catch (JsonException)
            {
                value = Null;
                return false;
            }
        }

        private sealed class Parser
        {
            private const int MaxDepth = 64;

            private readonly string _text;
            private int _pos;

            public Parser(string text)
            {
                _text = text;
            }

            public int Position => _pos;
            public bool AtEnd => _pos >= _text.Length;

            public void SkipWhitespace()
            {
                while (_pos < _text.Length)
                {
                    var c = _text[_pos];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n') _pos++;
                    else break;
                }
            }

            public JsonValue ReadValue(int depth)
            {
                // 深く入れ子にした入力で StackOverflow を起こさない。
                // 落ちるとしても例外で落ちるほうがまだ扱える。
                if (depth > MaxDepth) throw new JsonException("入れ子が深すぎます");

                SkipWhitespace();
                if (AtEnd) throw new JsonException("値が来る前に入力が終わりました");

                var c = _text[_pos];
                switch (c)
                {
                    case '{': return ReadObject(depth);
                    case '[': return ReadArray(depth);
                    case '"': return String(ReadString());
                    case 't': Expect("true"); return Bool(true);
                    case 'f': Expect("false"); return Bool(false);
                    case 'n': Expect("null"); return Null;
                    default: return ReadNumber();
                }
            }

            private JsonValue ReadObject(int depth)
            {
                _pos++; // {
                var result = NewObject();

                SkipWhitespace();
                if (!AtEnd && _text[_pos] == '}')
                {
                    _pos++;
                    return result;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd || _text[_pos] != '"') throw new JsonException($"キーが文字列ではありません (位置 {_pos})");

                    var key = ReadString();
                    SkipWhitespace();
                    if (AtEnd || _text[_pos] != ':') throw new JsonException($"':' がありません (位置 {_pos})");
                    _pos++;

                    result.Set(key, ReadValue(depth + 1));

                    SkipWhitespace();
                    if (AtEnd) throw new JsonException("オブジェクトが閉じていません");

                    if (_text[_pos] == ',')
                    {
                        _pos++;
                        continue;
                    }

                    if (_text[_pos] == '}')
                    {
                        _pos++;
                        return result;
                    }

                    throw new JsonException($"',' か '}}' が必要です (位置 {_pos})");
                }
            }

            private JsonValue ReadArray(int depth)
            {
                _pos++; // [
                var result = NewArray();

                SkipWhitespace();
                if (!AtEnd && _text[_pos] == ']')
                {
                    _pos++;
                    return result;
                }

                while (true)
                {
                    result.Add(ReadValue(depth + 1));

                    SkipWhitespace();
                    if (AtEnd) throw new JsonException("配列が閉じていません");

                    if (_text[_pos] == ',')
                    {
                        _pos++;
                        continue;
                    }

                    if (_text[_pos] == ']')
                    {
                        _pos++;
                        return result;
                    }

                    throw new JsonException($"',' か ']' が必要です (位置 {_pos})");
                }
            }

            private string ReadString()
            {
                _pos++; // "
                var sb = new StringBuilder();

                while (true)
                {
                    if (AtEnd) throw new JsonException("文字列が閉じていません");

                    var c = _text[_pos++];

                    if (c == '"') return sb.ToString();

                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }

                    if (AtEnd) throw new JsonException("エスケープが途中で終わりました");
                    var esc = _text[_pos++];

                    switch (esc)
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
                            if (_pos + 4 > _text.Length) throw new JsonException("\\u が短すぎます");
                            var hex = _text.Substring(_pos, 4);
                            if (!ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                                throw new JsonException($"\\u{hex} を解釈できません");
                            _pos += 4;
                            sb.Append((char)code);
                            break;
                        default:
                            throw new JsonException($"知らないエスケープ \\{esc} です");
                    }
                }
            }

            private JsonValue ReadNumber()
            {
                var start = _pos;
                if (!AtEnd && (_text[_pos] == '-' || _text[_pos] == '+')) _pos++;

                while (!AtEnd)
                {
                    var c = _text[_pos];
                    if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '-' || c == '+') _pos++;
                    else break;
                }

                var raw = _text.Substring(start, _pos - start);
                if (raw.Length == 0 ||
                    !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    throw new JsonException($"数値として読めません: '{raw}' (位置 {start})");
                }

                // 元の表記のまま持つ。1 を 1.0 にしないため。
                return new JsonValue(JsonKind.Number, raw);
            }

            private void Expect(string literal)
            {
                if (_pos + literal.Length > _text.Length ||
                    string.CompareOrdinal(_text, _pos, literal, 0, literal.Length) != 0)
                {
                    throw new JsonException($"'{literal}' が必要です (位置 {_pos})");
                }
                _pos += literal.Length;
            }
        }

        // ------------------------------------------------------------ 出力

        public override string ToString() => ToJson(true);

        public string ToJson(bool pretty = true)
        {
            var sb = new StringBuilder();
            Write(sb, this, pretty, 0);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, JsonValue value, bool pretty, int indent)
        {
            switch (value.Kind)
            {
                case JsonKind.Null:
                    sb.Append("null");
                    break;

                case JsonKind.Bool:
                    sb.Append(value._bool ? "true" : "false");
                    break;

                case JsonKind.Number:
                    sb.Append(value._raw);
                    break;

                case JsonKind.String:
                    WriteString(sb, value._raw);
                    break;

                case JsonKind.Array:
                    if (value._items.Count == 0)
                    {
                        sb.Append("[]");
                        break;
                    }
                    sb.Append('[');
                    for (var i = 0; i < value._items.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        NewLine(sb, pretty, indent + 1);
                        Write(sb, value._items[i], pretty, indent + 1);
                    }
                    NewLine(sb, pretty, indent);
                    sb.Append(']');
                    break;

                case JsonKind.Object:
                    if (value._keys.Count == 0)
                    {
                        sb.Append("{}");
                        break;
                    }
                    sb.Append('{');
                    for (var i = 0; i < value._keys.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        NewLine(sb, pretty, indent + 1);
                        WriteString(sb, value._keys[i]);
                        sb.Append(':');
                        if (pretty) sb.Append(' ');
                        Write(sb, value._members[value._keys[i]], pretty, indent + 1);
                    }
                    NewLine(sb, pretty, indent);
                    sb.Append('}');
                    break;
            }
        }

        private static void NewLine(StringBuilder sb, bool pretty, int indent)
        {
            if (!pretty) return;
            sb.Append('\n');
            sb.Append(' ', indent * 2);
        }

        private static void WriteString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c == 0x7f)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
