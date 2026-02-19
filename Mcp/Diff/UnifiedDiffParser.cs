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
            int lineIndex = 0;

            foreach (var raw in lines)
            {
                lineIndex++;

                // Ignorar la línea vacía final (artefacto del Split)
                if (raw.Length == 0 && last >= 0 && (lineIndex - 1) == last) break;

                if (raw.StartsWith("---")) diff.OriginalFile = raw;
                else if (raw.StartsWith("+++")) diff.NewFile = raw;
                else if (raw.StartsWith("@@"))
                {
                    var m = HunkHeader.Match(raw);
                    if (!m.Success)
                    {
                        throw new PatchException(
                            "Hunk inválido",
                            errorCode: "invalid_hunk_header",
                            hunkIndex: diff.Hunks.Count + 1,
                            diffLineNumber: lineIndex,
                            reason: "El encabezado del hunk no cumple el formato esperado.",
                            expectedFormat: "@@ -start[,count] +start[,count] @@",
                            problematicLine: Truncate(raw, 240));
                    }

                    current = new DiffHunk
                    {
                        StartOriginal = int.Parse(m.Groups[1].Value),
                        LengthOriginal = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1,
                        StartNew = int.Parse(m.Groups[3].Value),
                        LengthNew = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 1
                    };

                    diff.Hunks.Add(current);
                }
                else if (current != null)
                {
                    if (raw.Length == 0)
                    {
                        // Tratar línea vacía como contexto vacío (compatible con git/patch)
                        current.Lines.Add(" ");
                    }
                    else
                    {
                        // Espacios antes de + o - → diff inválido
                        if (raw.Length >= 2 && raw[0] == ' ' && (raw[1] == '+' || raw[1] == '-'))
                        {
                            throw new PatchException(
                                "Diff inválido: espacios antes del prefijo '+' o '-'.",
                                errorCode: "invalid_hunk_line_prefix",
                                hunkIndex: diff.Hunks.Count,
                                diffLineNumber: lineIndex,
                                reason: "Línea de hunk con espacios antes de '+' o '-'.",
                                expectedFormat: "Cada línea del hunk debe iniciar con ' ', '+' o '-'.",
                                problematicLine: Truncate(raw, 240));
                        }

                        char p = raw[0];
                        if (p != ' ' && p != '+' && p != '-')
                        {
                            throw new PatchException(
                                "Diff inválido: prefijo desconocido '" + p + "'.",
                                errorCode: "invalid_hunk_line_prefix",
                                hunkIndex: diff.Hunks.Count,
                                diffLineNumber: lineIndex,
                                reason: "Prefijo de línea inválido en hunk.",
                                expectedFormat: "Cada línea del hunk debe iniciar con ' ', '+' o '-'.",
                                problematicLine: Truncate(raw, 240));
                        }

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
                foreach (var line in h.Lines)
                {
                    if (line[0] == '+' || line[0] == '-')
                    {
                        hasChange = true;
                        break;
                    }
                }

                if (!hasChange)
                {
                    throw new PatchException(
                        "Diff inválido: hunk sin cambios reales.",
                        errorCode: "hunk_without_changes",
                        hunkIndex: i + 1,
                        reason: "El hunk contiene solo contexto, sin líneas '+' ni '-'.");
                }
            }

            return diff;
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

        static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max) return value;
            return value.Substring(0, max) + "...";
        }
    }
}
