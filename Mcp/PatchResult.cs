using System.Collections.Generic;
using McpHost.Diff;

namespace McpHost.Core
{
    // Resultado de file.apply_patch_only (o de su simulación con parse_only).
    class PatchResult
    {
        // Con parse_only son los del archivo actual (sirven para aplicar); si se aplicó, los del contenido
        // nuevo (sirven para el próximo patch sin releer).
        public string HashStrict { get; set; }
        public string HashNormalized { get; set; }
        public int TotalLines { get; set; }
        public string BackupPath { get; set; }
        public List<PatchHunkReport> Hunks { get; } = new List<PatchHunkReport>();
        public List<string> Warnings { get; } = new List<string>();

        public static PatchResult FromPlan(PatchPlan plan)
        {
            var result = new PatchResult();
            result.Warnings.AddRange(plan.Warnings);

            foreach (var hunk in plan.Hunks)
            {
                bool pureInsertion = hunk.OldCount == 0;
                var report = new PatchHunkReport
                {
                    Hunk = hunk.Number,
                    DeclaredLine = hunk.DeclaredStart,
                    // En una inserción pura es la línea DESPUÉS de la cual se insertó, igual que en "@@ -N,0".
                    AppliedLine = pureInsertion ? hunk.Start0 : hunk.Start0 + 1,
                    PureInsertion = pureInsertion,
                    IgnoredLeadingContext = hunk.IgnoredLeadingContext,
                    IgnoredTrailingContext = hunk.IgnoredTrailingContext
                };
                result.Hunks.Add(report);

                // Si el validador tuvo que ignorar contexto para ubicarlo, el modelo tiene que enterarse:
                // su idea del archivo no coincidía del todo con el archivo real.
                if (report.IgnoredLeadingContext > 0 || report.IgnoredTrailingContext > 0)
                    result.Warnings.Add(
                        "hunk " + report.Hunk + ": se ubicó en la línea " + report.AppliedLine +
                        " (declarada " + report.DeclaredLine + ") ignorando " + report.IgnoredLeadingContext +
                        " línea(s) de contexto inicial y " + report.IgnoredTrailingContext +
                        " final que no coincidían con el archivo. Verificá el resultado con file_read_range.");
            }

            return result;
        }
    }

    class PatchHunkReport
    {
        public int Hunk { get; set; }
        public int DeclaredLine { get; set; }
        public int AppliedLine { get; set; }
        public bool PureInsertion { get; set; }
        public int IgnoredLeadingContext { get; set; }
        public int IgnoredTrailingContext { get; set; }
    }
}
