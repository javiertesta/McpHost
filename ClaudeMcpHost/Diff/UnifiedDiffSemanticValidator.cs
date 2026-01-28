using System;
using McpHost.Utils;

namespace McpHost.Diff
{
    static class UnifiedDiffSemanticValidator
    {
        public static void ValidateAgainstText(UnifiedDiff diff, string originalText)
        {
            var lines = originalText.Split('\n');
            int lastEnd = 0;

            foreach (var hunk in diff.Hunks)
            {
                // 1️⃣ Orden y no superposición
                if (hunk.StartOriginal < lastEnd)
                    throw new InvalidOperationException(
                        "Hunks superpuestos o desordenados"
                    );

                lastEnd = hunk.StartOriginal + hunk.LengthOriginal;

                // 2️⃣ Validar contexto
                int idx = hunk.StartOriginal - 1;
                foreach (var l in hunk.Lines)
                {
                    if (l.StartsWith(" ") || l.StartsWith("-"))
                    {
                        string kind = l.StartsWith(" ") ? "Contexto del patch no coincide" : "Línea a eliminar no coincide";

                        if (idx >= lines.Length)
                        {
                            throw new InvalidOperationException(
                                kind + $" (EOF inesperado en línea {idx + 1}).\n" +
                                "CLAUDE: re-leé el bloque exacto y regenerá el diff preservando tabs y sin envolver líneas."
                            );
                        }

                        string fileLine = lines[idx] ?? "";
                        string diffLine = l.Length > 0 ? l.Substring(1) : "";

                        if (!SameLine(fileLine, diffLine))
                        {
                            string fileVis = TextDebugUtil.Truncate(TextDebugUtil.MakeVisible(fileLine), 240);
                            string diffVis = TextDebugUtil.Truncate(TextDebugUtil.MakeVisible(diffLine), 240);

                            throw new InvalidOperationException(
                                kind + $" en línea {idx + 1}.\n" +
                                $"Archivo: "{fileVis}"\n" +
                                $"Diff:    "{diffVis}"\n\n" +
                                "Nota: el MCP normaliza saltos de línea (LF/CRLF); el problema suele ser indentación (tabs vs espacios) " +
                                "o líneas partidas en el diff (no cortar líneas largas).\n" +
                                "CLAUDE: regenerá el diff sin partir líneas y preservando tabs."
                            );
                        }

                        idx++;
                    }
                    else if (l.StartsWith("+"))
                    {
                        // no avanza idx
                    }
                }
            }
        }

        static bool SameLine(string fileLine, string diffLine)
        {
            // 1) Comparación estricta (pero ignorando trailing whitespace)
            string a = (fileLine ?? "").TrimEnd(' ', '	');
            string b = (diffLine ?? "").TrimEnd(' ', '	');
            if (a == b) return true;

            // 2) Comparación tolerante: colapsar cualquier secuencia de espacios/tabs a un solo espacio.
            // Esto reduce falsos negativos cuando Claude cambia indentación (tabs vs espacios) o espaciado.
            string a2 = WhitespaceNormalizeUtil.NormalizeLineLoose(a);
            string b2 = WhitespaceNormalizeUtil.NormalizeLineLoose(b);
            return a2 == b2;
        }
    }
}
