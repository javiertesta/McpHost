namespace McpHost.Utils
{
    static class NewLineUtil
    {
        public static string Detect(string text)
        {
            if (string.IsNullOrEmpty(text)) return System.Environment.NewLine;
            if (text.Contains("\r\n")) return "\r\n";
            if (text.Contains("\n")) return "\n";
            if (text.Contains("\r")) return "\r";
            return System.Environment.NewLine;
        }
    }
}
