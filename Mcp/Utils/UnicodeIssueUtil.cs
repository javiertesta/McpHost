using System;
using System.Text;

namespace McpHost.Utils
{
    static class UnicodeIssueUtil
    {
        public static bool ContainsInvalidUnicode(string text)
        {
            if (text == null) return false;
            return text.IndexOf('\uFFFD') >= 0 || text.IndexOf('\uFEFF') >= 0;
        }

        public static string BuildInvalidUnicodeError(string text, string headline = null, int maxOccurrences = 5)
        {
            if (headline == null) headline = "Carácter Unicode inválido detectado (U+FFFD o U+FEFF).";

            var sb = new StringBuilder();
            sb.AppendLine(headline);
            sb.AppendLine();
            sb.AppendLine("Este error NO se resuelve reintentando el mismo apply-patch: fallará siempre mientras el archivo contenga esos caracteres.");
            sb.AppendLine("CLAUDE: NO INSISTAS. Pedile al usuario que corrija/elimine esos caracteres manualmente y luego reintente, o pasá a MODO MANUAL (mostrar el bloque exacto a editar para que el usuario lo aplique).");
            sb.AppendLine();

            // ubicaciones (primeros N)
            int found = 0;
            int line = 1;
            int col = 1;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (c == '\n')
                {
                    line++;
                    col = 1;
                    continue;
                }

                // Si viene CRLF, contar CR como salto y luego ignorar el LF
                if (c == '\r')
                {
                    line++;
                    col = 1;
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    continue;
                }

                if (c == '\uFFFD' || c == '\uFEFF')
                {
                    found++;
                    if (found <= maxOccurrences)
                    {
                        sb.AppendLine(" - " + DescribeChar(c) + " en línea " + line + ", columna " + col);
                    }
                }

                col++;
            }

            if (found > maxOccurrences)
            {
                sb.AppendLine(" - \u2026 y " + (found - maxOccurrences) + " ocurrencias más.");
            }

            return sb.ToString().TrimEnd();
        }

        static string DescribeChar(char c)
        {
            int code = (int)c;
            string name =
                c == '\uFFFD' ? "U+FFFD (REPLACEMENT CHARACTER)" :
                c == '\uFEFF' ? "U+FEFF (BOM / ZERO WIDTH NO-BREAK SPACE)" :
                "U+" + code.ToString("X4");
            return name;
        }
    }
}
