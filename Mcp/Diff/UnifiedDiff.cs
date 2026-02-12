using System.Collections.Generic;

namespace McpHost.Diff
{
    class UnifiedDiff
    {
        public string OriginalFile { get; set; }
        public string NewFile { get; set; }
        public List<DiffHunk> Hunks { get; } = new List<DiffHunk>();
    }

    class DiffHunk
    {
        public int StartOriginal { get; set; }
        public int LengthOriginal { get; set; }
        public int StartNew { get; set; }
        public int LengthNew { get; set; }
        public List<string> Lines { get; } = new List<string>();
    }
}
