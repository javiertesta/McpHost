using System;
using System.Text;
using System.Text.RegularExpressions;

namespace McpHost.Utils
{
    static class WhitespaceNormalizeUtil
    {
        static readonly Regex WhitespaceRun = new Regex(@"[\t ]+", RegexOptions.Compiled);
        /// <summary>
        /// Normaliza saltos de línea a LF y colapsa cualquier secuencia continua de espacios y/o tabs a un único espacio.
        /// Además, elimina whitespace al final de cada línea.
        /// Esto sirve para hashes y comparaciones "tolerantes" cuando la diferencia es solo de indentación o espaciado.
        /// </summary>
        public static string NormalizeForLooseHash(string text)
        {
            if (text == null) return "";

            string lf = NormalizeNewlinesToLf(text);

            var lines = lf.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                lines[i] = NormalizeLineLoose(lines[i]);
            }
            return string.Join("\n", lines);
        }

        public static string NormalizeLineLoose(string line)
        {
            if (line == null) return "";
            // Colapsar mezcla de espacios/tabs a un solo espacio (regex pre-compilado)
            string s = WhitespaceRun.Replace(line, " ");
            // Quitar trailing whitespace (después del colapso)
            s = s.TrimEnd(' ');
            return s;
        }

        public static string NormalizeNewlinesToLf(string text)
        {
            if (text == null) return "";
            return text.Replace("\r\n", "\n").Replace("\r", "\n");
        }
    }
}
