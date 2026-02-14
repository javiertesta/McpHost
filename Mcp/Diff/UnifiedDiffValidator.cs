using System;
using System.Linq;

namespace McpHost.Diff
{
    static class UnifiedDiffValidator
    {
        public static void Validate(
            UnifiedDiff diff,
            int originalLineCount,
            int maxTouchedLines = 200
        )
        {
            int touched =
                diff.Hunks.Sum(h => h.Lines.Count(l =>
                    l.StartsWith("+") || l.StartsWith("-")));

            if (touched == 0)
                throw new InvalidOperationException("Patch vacío");

            if (touched > maxTouchedLines)
                throw new InvalidOperationException("Patch demasiado grande");

            // El ratio "lineas tocadas / lineas originales" es una mala metrica en archivos chicos
            // (p.ej. archivos recien creados). Para evitar falsos positivos, solo lo evaluamos
            // a partir de cierto tamano.
            if (originalLineCount >= 150)
            {
                double maxRatio = 0.3;
                double ratio = (double)touched / originalLineCount;
                if (ratio > maxRatio)
                    throw new InvalidOperationException("Patch demasiado invasivo");
            }
        }
    }
}
