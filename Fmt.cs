// Quest Recorder - made by LethToxic.
using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace QuestRecorder
{
    /// <summary>Text helpers.</summary>
    internal static class Fmt
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>A coordinate with exactly 3 decimals, never "-0.000".</summary>
        public static string F3(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v))
                return "0.000";
            double r = Math.Round((double)v, 3, MidpointRounding.AwayFromZero);
            if (r == 0.0)
                r = 0.0; // turns -0 into 0
            return r.ToString("0.000", Inv);
        }

        /// <summary>Seconds with 3 decimals (the time of an event in the recording .csv), never "-0.000".</summary>
        public static string F3d(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
                return "0.000";
            double r = Math.Round(v, 3, MidpointRounding.AwayFromZero);
            if (r == 0.0)
                r = 0.0;
            return r.ToString("0.000", Inv);
        }

        public static string F1(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
                return "0.0";
            double r = Math.Round(v, 1, MidpointRounding.AwayFromZero);
            if (r == 0.0)
                r = 0.0;
            return r.ToString("0.0", Inv);
        }

        /// <summary>"x,y,z" exactly as profiles write XYZ.</summary>
        public static string Xyz(Vec3 p)
        {
            return F3(p.X) + "," + F3(p.Y) + "," + F3(p.Z);
        }

        /// <summary>16-digit upper-case hex with 0x, the way profiles write quest and area ids.</summary>
        public static string Hex(ulong id)
        {
            return "0x" + id.ToString("X16", Inv);
        }

        public static string Int(long v)
        {
            return v.ToString(Inv);
        }

        public static string UInt(ulong v)
        {
            return v.ToString(Inv);
        }

        public static string Format(string format, params object[] args)
        {
            if (format == null)
                return string.Empty;
            if (args == null || args.Length == 0)
                return format;
            try
            {
                return string.Format(Inv, format, args);
            }
            catch (FormatException)
            {
                return format;
            }
        }

        /// <summary>"12 m 05 s" style for durations.</summary>
        public static string Duration(double seconds)
        {
            if (seconds < 0)
                seconds = 0;
            if (seconds < 60)
                return F1(seconds) + " s";
            long total = (long)Math.Round(seconds);
            long h = total / 3600;
            long m = (total % 3600) / 60;
            long s = total % 60;
            if (h > 0)
                return h.ToString(Inv) + " h " + m.ToString("00", Inv) + " m";
            return m.ToString(Inv) + " m " + s.ToString("00", Inv) + " s";
        }

        /// <summary>"+mm:ss" (or "+h:mm:ss") since the recording started.</summary>
        public static string Elapsed(double seconds)
        {
            if (seconds < 0)
                seconds = 0;
            long total = (long)seconds;
            long h = total / 3600;
            long m = (total % 3600) / 60;
            long s = total % 60;
            if (h > 0)
                return "+" + h.ToString(Inv) + ":" + m.ToString("00", Inv) + ":" + s.ToString("00", Inv);
            return "+" + m.ToString("00", Inv) + ":" + s.ToString("00", Inv);
        }

        public static string Clock(DateTime t)
        {
            return t.ToString("HH:mm:ss", Inv);
        }

        public static string Stamp(DateTime t)
        {
            return t.ToString("yyyy-MM-dd HH:mm:ss.fff", Inv);
        }

        public static string Safe(string s)
        {
            return s ?? string.Empty;
        }

        /// <summary>One line: control characters (newlines, tabs) become spaces.</summary>
        public static string OneLine(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(char.IsControl(c) ? ' ' : c);
            return sb.ToString().Trim();
        }

        /// <summary>Escapes text for an XML attribute value in double quotes.</summary>
        public static string XmlAttr(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '&':
                        sb.Append("&amp;");
                        break;
                    case '<':
                        sb.Append("&lt;");
                        break;
                    case '>':
                        sb.Append("&gt;");
                        break;
                    case '"':
                        sb.Append("&quot;");
                        break;
                    default:
                        if (c == '\t' || c == '\r' || c == '\n')
                            sb.Append(' ');
                        else if (IsXmlChar(c))
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// A quoted string for a profile condition ('Lord Grathan\'s Key'), from the plain (not XML-escaped) text. The
        /// condition still goes through XmlAttr when it is written.
        /// </summary>
        public static string PyStr(string s)
        {
            return "'" + (s ?? string.Empty).Replace("\\", "\\\\").Replace("'", "\\'") + "'";
        }

        /// <summary>Text that is safe inside an XML comment: no "--", no trailing "-", one line.</summary>
        public static string XmlComment(string s)
        {
            string t = OneLine(s);
            var sb = new StringBuilder(t.Length);
            foreach (char c in t)
            {
                if (IsXmlChar(c))
                    sb.Append(c);
            }
            t = sb.ToString();
            while (t.Contains("--"))
                t = t.Replace("--", "- -");
            if (t.EndsWith("-", StringComparison.Ordinal))
                t += " ";
            return t;
        }

        private static bool IsXmlChar(char c)
        {
            if (c == '\t' || c == '\n' || c == '\r')
                return true;
            if (c < 0x20)
                return false;
            // surrogate pairs are copied as they are
            return c != '\uFFFE' && c != '\uFFFF';
        }

        /// <summary>One CSV field (quoted when it holds a comma, quote or line break).</summary>
        public static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;
            string t = s.Replace("\r", " ").Replace("\n", " ");
            if (t.IndexOf(',') >= 0 || t.IndexOf('"') >= 0 || t.IndexOf(';') >= 0)
                return "\"" + t.Replace("\"", "\"\"") + "\"";
            return t;
        }

        private static readonly string[] NumberWords =
        {
            "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
            "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty",
        };

        /// <summary>A name reduced for "is this the same place?" comparisons only.</summary>
        public static string LooseName(string s)
        {
            string t = OneLine(s).ToLowerInvariant();
            var words = new StringBuilder(t.Length);
            var word = new StringBuilder();
            for (int i = 0; i <= t.Length; i++)
            {
                char c = i < t.Length ? t[i] : ' ';
                if (char.IsLetterOrDigit(c))
                {
                    word.Append(c);
                    continue;
                }
                if (word.Length == 0)
                    continue;
                string w = word.ToString();
                int n = Array.IndexOf(NumberWords, w);
                if (words.Length > 0)
                    words.Append(' ');
                words.Append(n >= 0 ? n.ToString(Inv) : w);
                word.Clear();
            }
            return words.ToString();
        }

        /// <summary>A file-name-safe version of <paramref name="s"/> (Windows rules), never empty.</summary>
        public static string FileName(string s, string fallback)
        {
            string t = OneLine(s);
            if (t.Length == 0)
                t = fallback;
            var bad = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(t.Length);
            foreach (char c in t)
            {
                bool isBad = Array.IndexOf(bad, c) >= 0 || c == '\\' || c == '/' || c == ':' || c == '*' || c == '?' ||
                             c == '"' || c == '<' || c == '>' || c == '|';
                sb.Append(isBad ? '_' : c);
            }
            t = sb.ToString().Trim().TrimEnd('.');
            if (t.Length > 60)
                t = t.Substring(0, 60).Trim();
            return t.Length == 0 ? fallback : t;
        }
    }
}
