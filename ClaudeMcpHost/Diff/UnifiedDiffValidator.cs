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
            double maxRatio =
                originalLineCount <= 10 ? 1.0 :
                originalLineCount <= 50 ? 0.6 :
                0.3;

            int touched =
                diff.Hunks.Sum(h => h.Lines.Count(l =>
                    l.StartsWith("+") || l.StartsWith("-")));

            if (touched == 0)
                throw new InvalidOperationException("Patch vacío");

            if (touched > maxTouchedLines)
                throw new InvalidOperationException("Patch demasiado grande");

            if (originalLineCount > 0)
            {
                double ratio = (double)touched / originalLineCount;
                if (ratio > maxRatio)
                    throw new InvalidOperationException("Patch demasiado invasivo");
            }
        }
    }
}
