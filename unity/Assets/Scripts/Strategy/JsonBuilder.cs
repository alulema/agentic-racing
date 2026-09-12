using System.Globalization;
using System.Text;

namespace AgenticRacing.Strategy
{
    /// <summary>
    /// Minimal JSON writer for the strategy request body. Unity's
    /// <c>JsonUtility</c> can't emit <c>null</c> or omit fields, which the §6.3
    /// payload needs (a leader has no <c>gap_ahead</c>), so the request is built
    /// by hand here. Responses come back a fixed shape and are read with
    /// <c>JsonUtility.FromJson</c>. No dependency, identical under Mono and
    /// IL2CPP/WebGL.
    ///
    /// Commas are automatic: every value, key, and opening brace/bracket emits a
    /// separator first when one is due. Usage:
    /// <code>
    /// b.Obj()
    ///   .Field("event", "lap_completed")
    ///   .Key("me").Obj().Field("position", 3).EndObj()
    ///   .Arr("notes").Val("L2 T3 slow").EndArr()
    ///  .EndObj();
    /// </code>
    /// </summary>
    public sealed class JsonBuilder
    {
        private readonly StringBuilder _sb = new StringBuilder(512);
        private bool _needSep;

        public JsonBuilder Obj()
        {
            Sep();
            _sb.Append('{');
            _needSep = false;
            return this;
        }

        public JsonBuilder EndObj()
        {
            _sb.Append('}');
            _needSep = true;
            return this;
        }

        /// <summary>Open an array as the value of the current key, or as an array
        /// element when <paramref name="key"/> is null.</summary>
        public JsonBuilder Arr(string key = null)
        {
            if (key != null) Key(key);
            else Sep();
            _sb.Append('[');
            _needSep = false;
            return this;
        }

        public JsonBuilder EndArr()
        {
            _sb.Append(']');
            _needSep = true;
            return this;
        }

        /// <summary>Write ``"key":`` so the next Obj()/Arr()/value is its value.</summary>
        public JsonBuilder Key(string key)
        {
            Sep();
            WriteString(key);
            _sb.Append(':');
            _needSep = false;
            return this;
        }

        public JsonBuilder Field(string key, string value)
        {
            Key(key);
            WriteString(value);
            _needSep = true;
            return this;
        }

        public JsonBuilder Field(string key, int value)
        {
            Key(key);
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            _needSep = true;
            return this;
        }

        public JsonBuilder Field(string key, float value)
        {
            Key(key);
            _sb.Append(value.ToString("0.###", CultureInfo.InvariantCulture));
            _needSep = true;
            return this;
        }

        public JsonBuilder Field(string key, bool value)
        {
            Key(key);
            _sb.Append(value ? "true" : "false");
            _needSep = true;
            return this;
        }

        /// <summary>``"key": null`` — an absent optional value (§6.3).</summary>
        public JsonBuilder Null(string key)
        {
            Key(key);
            _sb.Append("null");
            _needSep = true;
            return this;
        }

        /// <summary>Bare string element inside an array.</summary>
        public JsonBuilder Val(string value)
        {
            Sep();
            WriteString(value);
            _needSep = true;
            return this;
        }

        /// <summary>Bare int element inside an array.</summary>
        public JsonBuilder Val(int value)
        {
            Sep();
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            _needSep = true;
            return this;
        }

        /// <summary>Embed an already-built JSON value verbatim as the value of
        /// <paramref name="key"/> — no re-escaping. Used by Fase 6.1 traceability
        /// to nest the exact request body sent to the strategist inside the
        /// <c>radio:msg</c> the DOM overlay stores, without paying to parse it
        /// back out of a string first. <paramref name="rawJson"/> must already be
        /// valid JSON (or null, written as the JSON null).</summary>
        public JsonBuilder Raw(string key, string rawJson)
        {
            Key(key);
            _sb.Append(string.IsNullOrEmpty(rawJson) ? "null" : rawJson);
            _needSep = true;
            return this;
        }

        public override string ToString() => _sb.ToString();

        // -- internals ------------------------------------------------------

        private void Sep()
        {
            if (_needSep) _sb.Append(',');
        }

        private void WriteString(string s)
        {
            if (s == null)
            {
                _sb.Append("null");
                return;
            }
            _sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\n': _sb.Append("\\n"); break;
                    case '\r': _sb.Append("\\r"); break;
                    case '\t': _sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) _sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else _sb.Append(c);
                        break;
                }
            }
            _sb.Append('"');
        }
    }
}
