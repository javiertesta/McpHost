using System;

namespace McpHost.Utils
{
    static class TextDebugUtil
    {
        public static string MakeVisible(string s)
        {
            if (s == null) return "";
            return s
                .Replace("\t", "\\t")
                .Replace("\r", "\\r");
        }

        public static string Truncate(string s, int maxLen)
        {
            if (s == null) return "";
            if (maxLen <= 0) return "";
            if (s.Length <= maxLen) return s;
            return s.Substring(0, maxLen) + "\u2026";
        }
    }
}
