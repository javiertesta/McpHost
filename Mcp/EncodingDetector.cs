using System.Text;

namespace McpHost.Core
{
    static class EncodingDetector
    {
        public static Encoding Detect(byte[] bytes, out bool hasBom)
        {
            // UTF-32 LE BOM
            if (bytes.Length >= 4 &&
                bytes[0] == 0xFF &&
                bytes[1] == 0xFE &&
                bytes[2] == 0x00 &&
                bytes[3] == 0x00)
            {
                hasBom = true;
                return new UTF32Encoding(false, true);
            }

            // UTF-32 BE BOM
            if (bytes.Length >= 4 &&
                bytes[0] == 0x00 &&
                bytes[1] == 0x00 &&
                bytes[2] == 0xFE &&
                bytes[3] == 0xFF)
            {
                hasBom = true;
                return new UTF32Encoding(true, true);
            }

            // UTF-16 LE BOM
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                hasBom = true;
                return new UnicodeEncoding(false, true);
            }

            // UTF-16 BE BOM
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                hasBom = true;
                return new UnicodeEncoding(true, true);
            }

            // UTF-8 BOM
            if (bytes.Length >= 3 &&
                bytes[0] == 0xEF &&
                bytes[1] == 0xBB &&
                bytes[2] == 0xBF)
            {
                hasBom = true;
                return new UTF8Encoding(true);
            }

            // UTF-8 sin BOM
            if (IsValidUtf8(bytes))
            {
                hasBom = false;
                return new UTF8Encoding(false);
            }

            hasBom = false;
            return Encoding.GetEncoding(1252); // Windows-1252
        }

        private static bool IsValidUtf8(byte[] bytes)
        {
            try
            {
                var utf8 = new UTF8Encoding(false, true);
                utf8.GetString(bytes);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
