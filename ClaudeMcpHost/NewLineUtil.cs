namespace McpHost.Utils
{
    static class NewLineUtil
    {
        public static string Detect(string text)
        {
            return text.Contains("\r\n") ? "\r\n" : "\n";
        }
    }
}
