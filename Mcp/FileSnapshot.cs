using System.Text;

namespace McpHost.Core
{
    class FileSnapshot
    {
        public string Path { get; set; }
        public byte[] OriginalBytes { get; set; }
        public Encoding Encoding { get; set; }
        public bool HasBom { get; set; }
        public string NewLine { get; set; }
        public string Text { get; set; }
        public string Sha256 { get; set; }
        public string Sha256NormalizedWhitespace { get; set; }
    }
}
