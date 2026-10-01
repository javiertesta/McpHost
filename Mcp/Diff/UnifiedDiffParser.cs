using System;
using System.Text.RegularExpressions;
using McpHost.Core;

namespace McpHost.Diff
{
    static class UnifiedDiffParser
    {
        // Soporta formatos: @@ -5,3 +5,3 @@ y también @@ -5 +5 @@ (git omite ,1 cuando count=1)
        static readonly Regex HunkHeader = new Regex(@"@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@");

        public static UnifiedDiff Parse(string diffText)
        {
            var diff = new UnifiedDiff();
            DiffHunk current = null;
            ValidateNoBom(diffText);

            var lines = diffText.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            int last = lines.Length - 1;
            int repeatedHeaders = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i];
                int lineIndex = i + 1;

                // Ignorar la línea vacía final (artefacto del Split)
                if (raw.Length == 0 && i == last) break;

                // Los encabezados de archivo sólo cuentan antes del primer hunk. Dentro de un hunk, "---"/"+++"
                // son el borrado/alta de líneas que empiezan con "--"/"++" (antes se descartaban y el patch
                // perdía esas líneas en silencio), salvo un encabezado repetido que se reconoce sin ambigüedad.
                if (current == null && raw.StartsWith("---")) diff.OriginalFile = raw;
                else if (current == null && raw.StartsWith("+++")) diff.NewFile = raw;
                else if (current != null && IsRepeatedFileHeader(diff, current, lines, i))
                {
                    repeatedHeaders++;
                    i++; // la línea "+++" del mismo encabezado
                }
                else if (raw.StartsWith("@@"))
                {
                    if (current != null && (current.OldNoNewlineAtEnd || current.NewNoNewlineAtEnd))
                        throw InvalidEofMarker("el hunk de la línea " + lineIndex + " viene después del marcador, que sólo puede ir en el último hunk.", diff.Hunks.Count, lineIndex, raw);

                    int startOriginal, lengthOriginal, startNew, lengthNew;
                    bool normalizedLegacyHeader;
                    bool headerOk = TryParseHunkHeader(raw, out startOriginal, out lengthOriginal, out startNew, out lengthNew, out normalizedLegacyHeader);

                    if (!headerOk)
                    {
                        throw new PatchException(
                            "Hunk inválido",
                            errorCode: "invalid_hunk_header",
                            hunkIndex: diff.Hunks.Count + 1,
                            diffLineNumber: lineIndex,
                            reason: "El encabezado del hunk no cumple el formato esperado ni pudo normalizarse automáticamente.",
                            expectedFormat: "@@ -start[,count] +start[,count] @@",
                            problematicLine: Truncate(raw, 240));
                    }

                    current = new DiffHunk
                    {
                        StartOriginal = startOriginal,
                        LengthOriginal = lengthOriginal,
                        StartNew = startNew,
                        LengthNew = lengthNew,
                        DeclaredStartOriginal = startOriginal,
                        DeclaredLengthOriginal = lengthOriginal,
                        DeclaredLengthNew = lengthNew,
                        HasNumericHeader = !normalizedLegacyHeader
                    };

                    if (normalizedLegacyHeader)
                    {
                        diff.NormalizedHunkHeaders++;
                    }
                    diff.Hunks.Add(current);
                }
                else if (current != null)
                {
                    if (raw.Length == 0)
                    {
                        throw new PatchException(
                            "Diff inválido: línea vacía en hunk sin prefijo.",
                            errorCode: "invalid_empty_hunk_line",
                            hunkIndex: diff.Hunks.Count,
                            diffLineNumber: lineIndex,
                            reason: "Cada línea dentro del hunk debe iniciar con ' ', '+' o '-'.",
                            expectedFormat: "No enviar líneas vacías dentro del hunk; para línea vacía de contexto usar ' ' y para cambios usar '+' o '-'.");
                    }
                    else if (raw.StartsWith("\\ No newline at end of file", StringComparison.Ordinal))
                    {
                        // Marcador estándar de unified diff. No es una línea del hunk: dice que la línea anterior es la
                        // última del archivo y no termina en salto de línea. Si la anterior es ' ', vale para los dos lados.
                        if (current.Lines.Count == 0)
                            throw InvalidEofMarker("en la línea " + lineIndex + " no hay ninguna línea del hunk antes del marcador.", diff.Hunks.Count, lineIndex, raw);
                        char anterior = current.Lines[current.Lines.Count - 1][0];
                        if (anterior != '+') current.OldNoNewlineAtEnd = true;
                        if (anterior != '-') current.NewNoNewlineAtEnd = true;
                    }
                    else
                    {
                        char p = raw[0];
                        if (p != ' ' && p != '+' && p != '-')
                        {
                            // "Index: x" (svn diff) o "diff --git a/x b/x" (git) después de un hunk: empieza el diff de otro archivo.
                            if (raw.StartsWith("Index: ", StringComparison.Ordinal) || raw.StartsWith("diff --git ", StringComparison.Ordinal))
                                throw new PatchException(
                                    "Diff inválido: contiene más de un archivo.",
                                    errorCode: "multi_file_diff",
                                    hunkIndex: diff.Hunks.Count,
                                    diffLineNumber: lineIndex,
                                    reason: "La línea " + lineIndex + " ('" + Truncate(raw, 120) + "') empieza el diff de otro archivo.",
                                    expectedFormat: "file.apply_patch_only modifica un solo archivo por llamada: mandá un diff por archivo (con svn diff, el bloque que empieza en su 'Index:').",
                                    problematicLine: Truncate(raw, 240));

                            bool pareceApplyPatch = raw.StartsWith("*** ", StringComparison.Ordinal);
                            throw new PatchException(
                                "Diff inválido: prefijo desconocido '" + p + "'.",
                                errorCode: pareceApplyPatch ? "invalid_diff_wrapper_format" : "invalid_hunk_line_prefix",
                                hunkIndex: diff.Hunks.Count,
                                diffLineNumber: lineIndex,
                                reason: pareceApplyPatch
                                    ? "Se detectó formato tipo apply_patch (*** Begin Patch / *** Update File / *** End Patch), pero esta herramienta espera unified diff puro."
                                    : "Prefijo de línea inválido en hunk.",
                                expectedFormat: pareceApplyPatch
                                    ? "Usar unified diff puro con encabezado @@ ... @@ y líneas que empiecen con ' ', '+' o '-'."
                                    : "Cada línea del hunk debe iniciar con ' ', '+' o '-'.",
                                problematicLine: Truncate(raw, 240));
                        }

                        // Después del marcador, ese lado del archivo ya terminó: no puede seguir ninguna línea suya.
                        if ((current.OldNoNewlineAtEnd && p != '+') || (current.NewNoNewlineAtEnd && p != '-'))
                            throw InvalidEofMarker("la línea " + lineIndex + " sigue del mismo lado del archivo después del marcador.", diff.Hunks.Count, lineIndex, raw);

                        current.Lines.Add(raw);
                    }
                }
            }

            if (diff.Hunks.Count == 0)
            {
                throw new PatchException(
                    "Diff sin hunks",
                    errorCode: "missing_hunks",
                    reason: "El diff no contiene encabezados '@@ ... @@'.",
                    expectedFormat: "Incluir al menos un hunk con encabezado @@ -start[,count] +start[,count] @@.");
            }

            for (int i = 0; i < diff.Hunks.Count; i++)
            {
                var h = diff.Hunks[i];
                bool hasChange = false;
                int originalConsumed = 0;
                int newProduced = 0;

                foreach (var line in h.Lines)
                {
                    if (line[0] == '+' || line[0] == '-')
                    {
                        hasChange = true;
                    }

                    if (line[0] == ' ' || line[0] == '-') originalConsumed++;
                    if (line[0] == ' ' || line[0] == '+') newProduced++;
                }

                if (!hasChange)
                {
                    throw new PatchException(
                        "Diff inválido: hunk sin cambios reales.",
                        errorCode: "hunk_without_changes",
                        hunkIndex: i + 1,
                        reason: "El hunk contiene solo contexto, sin líneas '+' ni '-'.");
                }

                if (originalConsumed != h.LengthOriginal || newProduced != h.LengthNew)
                {
                    h.LengthOriginal = originalConsumed;
                    h.LengthNew = newProduced;
                    diff.NormalizedHunkHeaders++;
                }
            }

            EnsureNoChangeLineWasLost(lines, diff, repeatedHeaders);

            return diff;
        }

        // Dentro de un hunk, "--- x" + "+++ y" + "@@" puede ser un encabezado repetido (o de otro archivo) o el
        // borrado/alta de líneas que empiezan con "--"/"++". Deciden la forma de las líneas y los contadores del
        // @@; si sigue siendo ambiguo se rechaza, porque adivinar mal pierde líneas del patch sin avisar.
        static bool IsRepeatedFileHeader(UnifiedDiff diff, DiffHunk current, string[] lines, int i)
        {
            if (i + 2 >= lines.Length) return false;
            string minus = lines[i];
            string plus = lines[i + 1];
            if (!minus.StartsWith("---") || !plus.StartsWith("+++") || !lines[i + 2].StartsWith("@@")) return false;
            if (!LooksLikeFileHeader(minus) || !LooksLikeFileHeader(plus)) return false;

            bool exactRepeat =
                diff.OriginalFile != null && diff.NewFile != null &&
                minus.TrimEnd() == diff.OriginalFile.TrimEnd() &&
                plus.TrimEnd() == diff.NewFile.TrimEnd();

            int consumedOld = 0;
            int producedNew = 0;
            foreach (var l in current.Lines)
            {
                if (l[0] == ' ' || l[0] == '-') consumedOld++;
                if (l[0] == ' ' || l[0] == '+') producedNew++;
            }
            bool completeBefore = current.HasNumericHeader &&
                consumedOld == current.DeclaredLengthOriginal && producedNew == current.DeclaredLengthNew;
            bool completeWithPair = current.HasNumericHeader &&
                consumedOld + 1 == current.DeclaredLengthOriginal && producedNew + 1 == current.DeclaredLengthNew;

            if (!exactRepeat && !completeBefore)
            {
                if (completeWithPair) return false;

                throw new PatchException(
                    "Diff inválido: encabezado ambiguo en la línea " + (i + 1) + ".",
                    errorCode: "ambiguous_file_header",
                    hunkIndex: diff.Hunks.Count,
                    diffLineNumber: i + 1,
                    reason: "Las líneas '---'/'+++' seguidas de '@@' pueden ser un encabezado repetido o el borrado/alta de líneas que empiezan con '--'/'++', y los contadores del @@ no alcanzan para decidir.",
                    expectedFormat: "Si es un encabezado repetido, quitalo (un solo '---'/'+++' al principio). Si es contenido, corregí los contadores del @@ o agregá una línea de contexto después.",
                    problematicLine: Truncate(minus, 240));
            }

            if (diff.OriginalFile != null &&
                !string.Equals(HeaderFileName(minus), HeaderFileName(diff.OriginalFile), StringComparison.OrdinalIgnoreCase))
            {
                throw new PatchException(
                    "Diff inválido: contiene más de un archivo.",
                    errorCode: "multi_file_diff",
                    hunkIndex: diff.Hunks.Count,
                    diffLineNumber: i + 1,
                    reason: "El encabezado de la línea " + (i + 1) + " nombra un archivo distinto del primero ('" + Truncate(diff.OriginalFile, 120) + "').",
                    expectedFormat: "file.apply_patch_only modifica un solo archivo por llamada: mandá un diff por archivo.",
                    problematicLine: Truncate(minus, 240));
            }

            return true;
        }

        // "--- a/x.vb", "+++ b/x.vb", "--- /dev/null", "--- D:\Repo\x.vb", "--- x.vb<TAB>fecha": sí.
        // "--- comentario viejo" (el borrado de "-- comentario viejo"): no, tiene espacios.
        static bool LooksLikeFileHeader(string line)
        {
            if (line.Length < 5 || (line[3] != ' ' && line[3] != '\t')) return false;

            string name = line.Substring(4);
            int tab = name.IndexOf('\t');
            if (tab >= 0) name = name.Substring(0, tab);
            name = name.Trim();

            if (name.Length == 0) return false;
            if (name == "/dev/null" || name.StartsWith("a/") || name.StartsWith("b/")) return true;
            if (Regex.IsMatch(name, @"^[A-Za-z]:[\\/]")) return true;
            return name.IndexOf(' ') < 0;
        }

        static string HeaderFileName(string headerLine)
        {
            string name = headerLine.Length > 4 ? headerLine.Substring(4) : string.Empty;
            int tab = name.IndexOf('\t');
            if (tab >= 0) name = name.Substring(0, tab);
            name = name.Trim().Replace('\\', '/');
            int slash = name.LastIndexOf('/');
            return slash >= 0 ? name.Substring(slash + 1) : name;
        }

        // Toda línea '+'/'-' posterior al primer @@ tiene que terminar en algún hunk (salvo las de encabezados
        // repetidos). Si no, el parser perdió líneas: mejor rechazar el diff que aplicarlo a medias.
        static void EnsureNoChangeLineWasLost(string[] lines, UnifiedDiff diff, int repeatedHeaders)
        {
            int expected = 0;
            bool insideHunks = false;
            foreach (var raw in lines)
            {
                if (raw.StartsWith("@@")) insideHunks = true;
                else if (insideHunks && raw.Length > 0 && (raw[0] == '+' || raw[0] == '-')) expected++;
            }
            expected -= 2 * repeatedHeaders;

            int parsed = 0;
            foreach (var hunk in diff.Hunks)
                foreach (var line in hunk.Lines)
                    if (line[0] == '+' || line[0] == '-') parsed++;

            if (parsed != expected)
                throw new PatchException(
                    "Error interno del parser: el diff trae " + expected + " líneas '+'/'-' y se leyeron " + parsed + ".",
                    errorCode: "parser_lost_lines",
                    reason: "Se rechaza el diff para no aplicar el patch a medias.");
        }

        static bool TryParseHunkHeader(
            string raw,
            out int startOriginal,
            out int lengthOriginal,
            out int startNew,
            out int lengthNew,
            out bool normalizedLegacyHeader)
        {
            var m = HunkHeader.Match(raw);
            if (m.Success)
            {
                startOriginal = int.Parse(m.Groups[1].Value);
                lengthOriginal = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1;
                startNew = int.Parse(m.Groups[3].Value);
                lengthNew = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 1;
                normalizedLegacyHeader = false;
                return true;
            }

            if (LooksLikeLegacyHunkHeader(raw))
            {
                startOriginal = 1;
                lengthOriginal = 0;
                startNew = 1;
                lengthNew = 0;
                normalizedLegacyHeader = true;
                return true;
            }

            startOriginal = lengthOriginal = startNew = lengthNew = 0;
            normalizedLegacyHeader = false;
            return false;
        }

        static bool LooksLikeLegacyHunkHeader(string raw)
        {
            string trimmed = (raw ?? string.Empty).Trim();
            if (string.Equals(trimmed, "@@", StringComparison.Ordinal)) return true;
            if (!trimmed.StartsWith("@@", StringComparison.Ordinal)) return false;
            if (!trimmed.EndsWith("@@", StringComparison.Ordinal)) return false;
            string body = trimmed.Substring(2, trimmed.Length - 4).Trim();
            return body.Length > 0 && body.IndexOf('-') < 0 && body.IndexOf('+') < 0;
        }

        static void ValidateNoBom(string diffText)
        {
            if (diffText.Length > 0 && diffText[0] == '\uFEFF')
            {
                throw new PatchException(
                    "Diff inválido: contiene BOM. El diff debe ser UTF-8 sin BOM.",
                    errorCode: "diff_contains_bom",
                    reason: "Se detectó BOM al inicio del contenido del diff.",
                    expectedFormat: "UTF-8 sin BOM.");
            }
        }

        static PatchException InvalidEofMarker(string detail, int hunkIndex, int lineIndex, string raw)
        {
            return new PatchException(
                "Diff inválido: marcador '\\ No newline at end of file' mal ubicado: " + detail,
                errorCode: "invalid_eof_marker",
                hunkIndex: hunkIndex,
                diffLineNumber: lineIndex,
                reason: "El marcador dice que la línea anterior es la última del archivo y no termina en salto de línea.",
                expectedFormat: "Va justo después de la última línea del archivo (la vieja con ' ' o '-', la nueva con ' ' o '+') y sólo en el último hunk. Si no querés cambiar el salto de línea final, sacalo.",
                problematicLine: Truncate(raw, 240));
        }

        static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max) return value;
            return value.Substring(0, max) + "...";
        }
    }
}
