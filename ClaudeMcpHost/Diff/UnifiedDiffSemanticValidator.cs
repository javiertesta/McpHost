using System;

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
                    if (l.StartsWith(" "))
                    {
                        if (idx >= lines.Length ||
                            lines[idx] != l.Substring(1))
                        {
                            throw new InvalidOperationException(
                                "Contexto del patch no coincide"
                            );
                        }
                        idx++;
                    }
                    else if (l.StartsWith("-"))
                    {
                        if (idx >= lines.Length ||
                            lines[idx] != l.Substring(1))
                        {
                            throw new InvalidOperationException(
                                "Línea a eliminar no coincide"
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
    }
}
