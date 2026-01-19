using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace McpHost.Core
{
    static class FileSnapshotLoader
    {
        public static FileSnapshot Load(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var (encoding, hasBom) = DetectEncoding(bytes);
            string text;

            try
            {
                int offset = hasBom ? encoding.GetPreamble().Length : 0;
                text = encoding.GetString(bytes, offset, bytes.Length - offset);
            }
            catch
            {
                throw new InvalidOperationException(
                    "No se pudo decodificar el archivo con el encoding detectado"
                );
            }

            string newline = DetectNewLine(text);

            return new FileSnapshot
            {
                Path = path,
                OriginalBytes = bytes,
                Encoding = encoding,
                HasBom = hasBom,
                NewLine = newline,
                Text = text,
                Sha256 = ComputeSha256(bytes)
            };
        }

        static (Encoding encoding, bool hasBom) DetectEncoding(byte[] bytes)
        {
            if (bytes.Length >= 3 &&
                bytes[0] == 0xEF &&
                bytes[1] == 0xBB &&
                bytes[2] == 0xBF)
                return (Encoding.UTF8, true);

            // UTF-16 LE BOM
            if (bytes.Length >= 2 &&
                bytes[0] == 0xFF &&
                bytes[1] == 0xFE)
                return (Encoding.Unicode, true);

            // UTF-16 BE BOM
            if (bytes.Length >= 2 &&
                bytes[0] == 0xFE &&
                bytes[1] == 0xFF)
                return (Encoding.BigEndianUnicode, true);

            // Sin BOM → intentar UTF-8 estricto
            try
            {
                var utf8Strict = new UTF8Encoding(false, true);
                utf8Strict.GetString(bytes);
                return (utf8Strict, false);
            }
            catch
            {
                throw new InvalidOperationException("El archivo no tiene BOM y no es UTF-8 válido. Encoding no soportado.");
            }
        }

        static string DetectNewLine(string text)
        {
            if (text.Contains("\r\n")) return "\r\n";
            if (text.Contains("\n")) return "\n";
            if (text.Contains("\r")) return "\r";
            return Environment.NewLine;
        }

        static string ComputeSha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                return BitConverter.ToString(hash).Replace("-", "");
            }
        }

    }
}
