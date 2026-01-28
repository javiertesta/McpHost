using System;
using System.Text.RegularExpressions;

namespace McpHost.Diff
{
    static class UnifiedDiffParser
    {
        static readonly Regex HunkHeader = new Regex(@"@@ -(\d+),(\d+) \+(\d+),(\d+) @@");

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
                // Ignorar la línea vacía final (artefacto del Split)
                if (raw.Length == 0 && last >= 0 && lineIndex == last) break;

                if (raw.StartsWith("---")) diff.OriginalFile = raw;
                else if (raw.StartsWith("+++")) diff.NewFile = raw;
                else if (raw.StartsWith("@@"))
                {
                    var m = HunkHeader.Match(raw);
                    if (!m.Success) throw new InvalidOperationException("Hunk inválido");

                    current = new DiffHunk
                    {
                        StartOriginal = int.Parse(m.Groups[1].Value),
                        LengthOriginal = int.Parse(m.Groups[2].Value),
                        StartNew = int.Parse(m.Groups[3].Value),
                        LengthNew = int.Parse(m.Groups[4].Value)
                    };

                    diff.Hunks.Add(current);
                }
                else if (current != null)
                {
                    if (raw.Length == 0)
                        // Tratar línea vacía como contexto vacío (compatible con git/patch)
                        current.Lines.Add(" ");
                    else
                    {
                        // Espacios antes de + o - → diff inválido
                        if (raw.Length >= 2 && raw[0] == ' ' && (raw[1] == '+' || raw[1] == '-')) throw new InvalidOperationException("Diff inválido: espacios antes del prefijo '+' o '-'.");
                        char p = raw[0];
                        if (p != ' ' && p != '+' && p != '-') throw new InvalidOperationException("Diff inválido: prefijo desconocido '" + p + "'.");
                        current.Lines.Add(raw);
                    }
                }
                lineIndex++;
            }

            if (diff.Hunks.Count == 0) throw new InvalidOperationException("Diff sin hunks");
            
            foreach (var h in diff.Hunks)
            {
                bool hasChange = false;
                foreach (var line in h.Lines)
                {
                    if (line[0] == '+' || line[0] == '-')
                    {
                        hasChange = true;
                        break;
                    }
                }
                if (!hasChange) throw new InvalidOperationException("Diff inválido: hunk sin cambios reales.");
            }

            return diff;
        }

        static void ValidateNoBom(string diffText) {
            if (diffText.Length > 0 && diffText[0] == '\uFEFF') throw new InvalidOperationException("Diff inválido: contiene BOM. El diff debe ser UTF-8 sin BOM.");
        }

    }
}
