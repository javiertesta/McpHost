using System.Collections.Generic;

namespace McpHost.Diff
{
    class UnifiedDiff
    {
        public string OriginalFile { get; set; }
        public string NewFile { get; set; }
        public int NormalizedHunkHeaders { get; set; }
        public List<DiffHunk> Hunks { get; } = new List<DiffHunk>();
    }

    class DiffHunk
    {
        public int StartOriginal { get; set; }
        public int LengthOriginal { get; set; }
        public int StartNew { get; set; }
        public int LengthNew { get; set; }
        public List<string> Lines { get; } = new List<string>();

        // Tal como vinieron en el @@ recibido, antes de normalizar contadores o reubicar el hunk.
        public int DeclaredStartOriginal { get; set; }
        public int DeclaredLengthOriginal { get; set; }
        public int DeclaredLengthNew { get; set; }
        public bool HasNumericHeader { get; set; }

        // Líneas de contexto que el validador tuvo que ignorar (fuzz) para ubicar el hunk.
        public int IgnoredLeadingContext { get; set; }
        public int IgnoredTrailingContext { get; set; }
    }
}
