using System;
using System.Collections.Generic;
using System.Linq;
using McpHost.Utils;

namespace McpHost.Diff
{
    static class UnifiedDiffSemanticValidator
    {
        // Búsqueda por contexto para reubicar hunks cuando el @@ (StartOriginal) está corrido.
        // Fuzz: recorta SOLO contexto al inicio/fin (líneas que empiezan con ' ') para tolerar pequeños desajustes.
        const int DefaultSearchWindow = 300;
        const int MaxSearchWindow = 2000;
        const int MaxFuzz = 2;

        public static void ValidateAgainstText(UnifiedDiff diff, string originalText)
        {
            var fileLines = originalText.Split('\n');
            int lastEndIdx = 0; // 0-based, exclusivo

            foreach (var hunk in diff.Hunks)
            {
                int declaredStartIdx = Math.Max(hunk.StartOriginal - 1, lastEndIdx);

                // 1) Intento estricto: exactamente donde dice el @@ (pero respetando orden)
                if (TryValidateAt(fileLines, declaredStartIdx, hunk.Lines, out Counts strictCounts, out string strictError))
                {
                    hunk.StartOriginal = declaredStartIdx + 1;
                    hunk.LengthOriginal = strictCounts.OriginalConsumed;
                    hunk.LengthNew = strictCounts.NewProduced;
                    lastEndIdx = declaredStartIdx + strictCounts.OriginalConsumed;
                    continue;
                }

                // 2) Reubicar el hunk buscando por contexto (similar a GNU patch), con fuzz controlado.
                if (!TryFindUniqueHunkStart(
                        fileLines,
                        hunk.Lines,
                        declaredStartIdx,
                        lastEndIdx,
                        out IList<string> effectiveLines,
                        out int foundStartIdx,
                        out Counts alignedCounts,
                        out string alignError))
                {
                    throw new InvalidOperationException(
                        strictError + "\n\n" +
                        "Nota: intenté reubicar el hunk por contexto (búsqueda + fuzz controlado) y no encontré una coincidencia única.\n" +
                        alignError
                    );
                }

                // Ajustar el hunk in-place para que el applier use la posición real.
                hunk.StartOriginal = foundStartIdx + 1;
                hunk.LengthOriginal = alignedCounts.OriginalConsumed;
                hunk.LengthNew = alignedCounts.NewProduced;

                if (!ReferenceEquals(effectiveLines, hunk.Lines))
                {
                    hunk.Lines.Clear();
                    hunk.Lines.AddRange(effectiveLines);
                }

                lastEndIdx = foundStartIdx + alignedCounts.OriginalConsumed;
            }
        }

        struct Counts
        {
            public int OriginalConsumed;
            public int NewProduced;
        }

        static bool TryValidateAt(
            string[] fileLines,
            int startIdx,
            IList<string> hunkLines,
            out Counts counts,
            out string errorMessage)
        {
            counts = new Counts();
            errorMessage = null;

            int idx = startIdx;
            int originalConsumed = 0;
            int newProduced = 0;

            foreach (var l in hunkLines)
            {
                char p = l.Length > 0 ? l[0] : '\0';

                if (p == ' ' || p == '-')
                {
                    string kind = p == ' ' ? "Contexto del patch no coincide" : "Línea a eliminar no coincide";

                    if (idx >= fileLines.Length)
                    {
                        errorMessage =
                            kind + $" (EOF inesperado en línea {idx + 1}).\n" +
                            "CLAUDE: re-leé el bloque exacto y regenerá el diff preservando tabs y sin envolver líneas.";
                        return false;
                    }

                    string fileLine = fileLines[idx] ?? "";
                    string diffLine = l.Length > 1 ? l.Substring(1) : "";

                    if (!SameLine(fileLine, diffLine))
                    {
                        string fileVis = TextDebugUtil.Truncate(TextDebugUtil.MakeVisible(fileLine), 240);
                        string diffVis = TextDebugUtil.Truncate(TextDebugUtil.MakeVisible(diffLine), 240);

                        errorMessage =
                            kind + $" en línea {idx + 1}.\n" +
                            $"Archivo: \"{fileVis}\"\n" +
                            $"Diff:    \"{diffVis}\"\n\n" +
                            "Nota: el MCP normaliza saltos de línea (LF/CRLF); el problema suele ser indentación (tabs vs espacios) o líneas partidas en el diff (no cortar líneas largas).\n" +
                            "CLAUDE: regenerá el diff sin partir líneas y preservando tabs.";
                        return false;
                    }

                    idx++;
                    originalConsumed++;
                    if (p == ' ') newProduced++;
                }
                else if (p == '+')
                {
                    newProduced++;
                }
            }

            counts = new Counts { OriginalConsumed = originalConsumed, NewProduced = newProduced };
            return true;
        }

        static bool TryFindUniqueHunkStart(
            string[] fileLines,
            IList<string> originalHunkLines,
            int expectedStartIdx,
            int minStartIdx,
            out IList<string> effectiveLines,
            out int foundStartIdx,
            out Counts counts,
            out string errorMessage)
        {
            effectiveLines = originalHunkLines;
            foundStartIdx = -1;
            counts = new Counts();
            errorMessage = null;

            int baseConsumed = CountOriginalConsumed(originalHunkLines);
            int window = Math.Min(MaxSearchWindow, Math.Max(DefaultSearchWindow, baseConsumed * 20));

            foreach (var variant in EnumerateFuzzVariants(originalHunkLines, MaxFuzz))
            {
                var hits = FindMatches(fileLines, variant, expectedStartIdx, minStartIdx, window);
                if (hits.Count == 0)
                {
                    // Fallback: si el @@ está MUY corrido, buscar en todo el archivo.
                    hits = FindMatches(fileLines, variant, expectedStartIdx, minStartIdx, int.MaxValue);
                }

                if (hits.Count == 1)
                {
                    int startIdx = hits[0];
                    if (!TryValidateAt(fileLines, startIdx, variant, out counts, out errorMessage))
                        return false;

                    effectiveLines = variant;
                    foundStartIdx = startIdx;
                    return true;
                }

                if (hits.Count > 1)
                {
                    string sample = string.Join(", ", hits.Take(6).Select(h => (h + 1).ToString()));
                    string more = hits.Count > 6 ? $" (+{hits.Count - 6} más)" : "";
                    errorMessage =
                        "El hunk coincide en múltiples ubicaciones del archivo (" + sample + more + ").\n" +
                        "Para mantener la seguridad del MCP, no elijo una al azar.\n" +
                        "CLAUDE: agregá 3-6 líneas de contexto real alrededor del cambio y regenerá el diff sin envolver líneas.";
                    return false;
                }
            }

            errorMessage =
                "No se encontró una ubicación donde el contexto del hunk coincida (ni con fuzz controlado).\n" +
                "Causas típicas: líneas partidas en el diff, contexto inventado/no exacto, o el diff fue generado desde un rango distinto.\n" +
                "CLAUDE: re-leé el bloque exacto y regenerá el diff preservando tabs y sin envolver líneas.";
            return false;
        }

        static IEnumerable<IList<string>> EnumerateFuzzVariants(IList<string> hunkLines, int maxFuzz)
        {
            // (0,0) siempre
            yield return hunkLines;

            int firstChange = -1;
            int lastChange = -1;
            for (int i = 0; i < hunkLines.Count; i++)
            {
                char p = hunkLines[i].Length > 0 ? hunkLines[i][0] : '\0';
                if (p == '+' || p == '-')
                {
                    if (firstChange == -1) firstChange = i;
                    lastChange = i;
                }
            }
            if (firstChange == -1 || lastChange == -1) yield break;

            int leadingContext = 0;
            for (int i = 0; i < firstChange; i++)
            {
                if (hunkLines[i].StartsWith(" ")) leadingContext++;
                else break;
            }

            int trailingContext = 0;
            for (int i = hunkLines.Count - 1; i > lastChange; i--)
            {
                if (hunkLines[i].StartsWith(" ")) trailingContext++;
                else break;
            }

            int maxLead = Math.Min(maxFuzz, leadingContext);
            int maxTrail = Math.Min(maxFuzz, trailingContext);

            // Orden: menor fuzz total primero
            for (int fuzzTotal = 1; fuzzTotal <= maxLead + maxTrail; fuzzTotal++)
            {
                for (int fuzzLead = 0; fuzzLead <= maxLead; fuzzLead++)
                {
                    int fuzzTrail = fuzzTotal - fuzzLead;
                    if (fuzzTrail < 0 || fuzzTrail > maxTrail) continue;

                    int start = fuzzLead;
                    int count = hunkLines.Count - fuzzLead - fuzzTrail;
                    if (count <= 0) continue;

                    var eff = new List<string>(count);
                    for (int i = 0; i < count; i++)
                        eff.Add(hunkLines[start + i]);

                    yield return eff;
                }
            }
        }

        static List<int> FindMatches(
            string[] fileLines,
            IList<string> hunkLines,
            int expectedStartIdx,
            int minStartIdx,
            int window)
        {
            int start = minStartIdx;
            int end = fileLines.Length - 1;

            if (window != int.MaxValue)
            {
                start = Math.Max(minStartIdx, expectedStartIdx - window);
                end = Math.Min(fileLines.Length - 1, expectedStartIdx + window);
            }

            var hits = new List<int>();
            for (int i = start; i <= end; i++)
            {
                if (MatchesAt(fileLines, i, hunkLines))
                {
                    hits.Add(i);
                    if (window == int.MaxValue && hits.Count > 10) break;
                }
            }
            return hits;
        }

        static bool MatchesAt(string[] fileLines, int startIdx, IList<string> hunkLines)
        {
            int idx = startIdx;
            foreach (var l in hunkLines)
            {
                char p = l.Length > 0 ? l[0] : '\0';
                if (p == ' ' || p == '-')
                {
                    if (idx >= fileLines.Length) return false;
                    string fileLine = fileLines[idx] ?? "";
                    string diffLine = l.Length > 1 ? l.Substring(1) : "";
                    if (!SameLine(fileLine, diffLine)) return false;
                    idx++;
                }
            }
            return true;
        }

        static int CountOriginalConsumed(IList<string> hunkLines)
        {
            int c = 0;
            foreach (var l in hunkLines)
            {
                char p = l.Length > 0 ? l[0] : '\0';
                if (p == ' ' || p == '-') c++;
            }
            return c;
        }

        static bool SameLine(string fileLine, string diffLine)
        {
            // 1) Comparación estricta (pero ignorando trailing whitespace)
            string a = (fileLine ?? "").TrimEnd(' ', '\t');
            string b = (diffLine ?? "").TrimEnd(' ', '\t');
            if (a == b) return true;

            // 2) Comparación tolerante: colapsar cualquier secuencia de espacios/tabs a un solo espacio.
            // Esto reduce falsos negativos cuando Claude cambia indentación (tabs vs espacios) o espaciado.
            string a2 = WhitespaceNormalizeUtil.NormalizeLineLoose(a);
            string b2 = WhitespaceNormalizeUtil.NormalizeLineLoose(b);
            return a2 == b2;
        }
    }
}
