using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AstraKingdoms.Client.Localization
{
    /// <summary>CLDR plural category used by <see cref="MessageFormat"/> (integers only).</summary>
    public enum PluralCategory : byte
    {
        Other = 0,
        One = 1,
    }

    /// <summary>
    /// Integer plural rules for the V1 languages (CLDR cardinal rules restricted to whole numbers):
    /// English "one" is exactly 1; Hindi and Kannada "one" is 0 or 1. Unknown languages use English.
    /// </summary>
    public static class PluralRules
    {
        public static PluralCategory Select(string language, long n)
        {
            switch (language)
            {
                case Localizer.Hindi:
                case Localizer.Kannada:
                    return n == 0 || n == 1 ? PluralCategory.One : PluralCategory.Other;
                default:
                    return n == 1 ? PluralCategory.One : PluralCategory.Other;
            }
        }
    }

    /// <summary>
    /// Key-value message formatting without string concatenation (plan: "All player-facing strings use
    /// keys and parameters"). A template is literal text with placeholders:
    /// <list type="bullet">
    /// <item><c>{0}</c> - argument 0, invariant text (numbers without grouping).</item>
    /// <item><c>{0,number}</c> - an integer with the language's digit grouping (English 51,040;
    /// Hindi/Kannada Indian grouping 1,23,456).</item>
    /// <item><c>{0,plural,one{# cell} other{# cells}}</c> - a sub-message chosen by the plural
    /// category of integer argument 0; <c>#</c> is the number. Sub-messages may hold other
    /// placeholders. <c>other</c> is mandatory (the fallback category).</item>
    /// </list>
    /// Literal braces are not supported (no player text needs them). Malformed templates throw
    /// <see cref="FormatException"/>; <see cref="Validate"/> reports the problem without throwing.
    /// </summary>
    public static class MessageFormat
    {
        public static string Format(string template, string language, params object[] args)
        {
            if (template == null) throw new ArgumentNullException(nameof(template));
            var sb = new StringBuilder(template.Length + 16);
            int pos = 0;
            Render(template, ref pos, sb, language, args ?? Array.Empty<object>(), null, nested: false);
            return sb.ToString();
        }

        /// <summary>Null when the template is well formed; otherwise the first problem found.</summary>
        public static string Validate(string template)
        {
            if (template == null) return "null template";
            try
            {
                int pos = 0;
                var sb = new StringBuilder();
                Render(template, ref pos, sb, Localizer.English, null, null, nested: false);
                return null;
            }
            catch (FormatException e)
            {
                return e.Message;
            }
        }

        /// <summary>Highest argument index used + 1 (0 when none). Throws for malformed templates.</summary>
        public static int ArgumentCount(string template)
        {
            var used = new List<int>();
            int pos = 0;
            Render(template, ref pos, new StringBuilder(), Localizer.English, null, used, nested: false);
            int max = -1;
            foreach (int i in used) max = Math.Max(max, i);
            return max + 1;
        }

        /// <summary>Integer with the language's grouping: 3-digit groups for English; Indian 3-then-2 for hi/kn.</summary>
        public static string GroupedInteger(long value, string language)
        {
            bool negative = value < 0;
            string digits = negative ? (-(decimal)value).ToString(CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);
            bool indian = language == Localizer.Hindi || language == Localizer.Kannada;
            var sb = new StringBuilder(digits.Length + 8);
            int len = digits.Length;
            for (int i = 0; i < len; i++)
            {
                int remaining = len - i;
                sb.Append(digits[i]);
                if (remaining <= 1) continue;
                int after = remaining - 1; // digits still to come
                bool separator = indian ? after == 3 || (after > 3 && (after - 3) % 2 == 0) : after % 3 == 0;
                if (separator) sb.Append(',');
            }
            return negative ? "-" + sb : sb.ToString();
        }

        // Renders (or, with args == null, only validates) from pos until the end, or until the
        // closing '}' of a sub-message when nested.
        private static void Render(string t, ref int pos, StringBuilder sb, string language, object[] args, List<int> used, bool nested,
            string hashValue = null)
        {
            while (pos < t.Length)
            {
                char c = t[pos];
                if (c == '}')
                {
                    if (nested) return;
                    throw new FormatException("Unmatched '}' at " + pos + ".");
                }
                if (c == '#' && hashValue != null)
                {
                    sb.Append(hashValue);
                    pos++;
                    continue;
                }
                if (c != '{')
                {
                    sb.Append(c);
                    pos++;
                    continue;
                }
                Placeholder(t, ref pos, sb, language, args, used);
            }
            if (nested) throw new FormatException("Unclosed sub-message.");
        }

        private static void Placeholder(string t, ref int pos, StringBuilder sb, string language, object[] args, List<int> used)
        {
            int start = pos;
            pos++; // '{'
            int index = ReadIndex(t, ref pos);
            used?.Add(index);
            object arg = null;
            if (args != null)
            {
                if (index >= args.Length) throw new FormatException("Placeholder {" + index + "} has no argument.");
                arg = args[index];
            }
            SkipSpaces(t, ref pos);
            if (pos < t.Length && t[pos] == '}')
            {
                pos++;
                if (args != null) sb.Append(Convert.ToString(arg, CultureInfo.InvariantCulture));
                return;
            }
            Expect(t, ref pos, ',');
            string type = ReadWord(t, ref pos);
            SkipSpaces(t, ref pos);
            if (type == "number")
            {
                Expect(t, ref pos, '}');
                if (args != null) sb.Append(GroupedInteger(ToLong(arg, index), language));
                return;
            }
            if (type != "plural") throw new FormatException("Unknown placeholder type '" + type + "' at " + start + ".");
            Expect(t, ref pos, ',');
            long n = args != null ? ToLong(arg, index) : 0;
            PluralCategory wanted = args != null ? PluralRules.Select(language, n) : PluralCategory.Other;
            bool sawOther = false, rendered = false;
            string chosenOther = null;
            while (true)
            {
                SkipSpaces(t, ref pos);
                if (pos >= t.Length) throw new FormatException("Unclosed plural at " + start + ".");
                if (t[pos] == '}')
                {
                    pos++;
                    break;
                }
                string category = ReadWord(t, ref pos);
                PluralCategory cat;
                if (category == "one") cat = PluralCategory.One;
                else if (category == "other") cat = PluralCategory.Other;
                else throw new FormatException("Unknown plural category '" + category + "'.");
                if (cat == PluralCategory.Other) sawOther = true;
                SkipSpaces(t, ref pos);
                Expect(t, ref pos, '{');
                var sub = new StringBuilder();
                Render(t, ref pos, sub, language, args, used, nested: true, hashValue: n.ToString(CultureInfo.InvariantCulture));
                Expect(t, ref pos, '}');
                if (cat == wanted && !rendered)
                {
                    sb.Append(sub);
                    rendered = true;
                }
                if (cat == PluralCategory.Other) chosenOther = sub.ToString();
            }
            if (!sawOther) throw new FormatException("A plural needs an 'other' form (at " + start + ").");
            // Fallback rule: a missing category uses 'other'.
            if (!rendered && args != null) sb.Append(chosenOther);
        }

        private static long ToLong(object arg, int index)
        {
            switch (arg)
            {
                case int i: return i;
                case long l: return l;
                case short s: return s;
                case byte b: return b;
                case uint u: return u;
                default: throw new FormatException("Placeholder {" + index + "} needs an integer argument.");
            }
        }

        private static int ReadIndex(string t, ref int pos)
        {
            SkipSpaces(t, ref pos);
            int start = pos, n = 0;
            while (pos < t.Length && t[pos] >= '0' && t[pos] <= '9')
            {
                n = checked(n * 10 + (t[pos] - '0'));
                pos++;
            }
            if (pos == start) throw new FormatException("Expected an argument index at " + start + ".");
            return n;
        }

        private static string ReadWord(string t, ref int pos)
        {
            SkipSpaces(t, ref pos);
            int start = pos;
            while (pos < t.Length && ((t[pos] >= 'a' && t[pos] <= 'z') || (t[pos] >= 'A' && t[pos] <= 'Z'))) pos++;
            if (pos == start) throw new FormatException("Expected a word at " + start + ".");
            return t.Substring(start, pos - start);
        }

        private static void Expect(string t, ref int pos, char c)
        {
            SkipSpaces(t, ref pos);
            if (pos >= t.Length || t[pos] != c) throw new FormatException("Expected '" + c + "' at " + pos + ".");
            pos++;
        }

        private static void SkipSpaces(string t, ref int pos)
        {
            while (pos < t.Length && t[pos] == ' ') pos++;
        }
    }
}
