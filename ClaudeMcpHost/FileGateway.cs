using McpHost.Diff;
using McpHost.Utils;
using System;
using System.IO;
using System.Text;
using static System.Net.Mime.MediaTypeNames;

namespace McpHost.Core
{
    class FileGateway
    {
        public FileSnapshot Read(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);

            bool hasBom;
            Encoding enc = EncodingDetector.Detect(bytes, out hasBom);

            // Saltar BOM al decodificar para que el texto no incluya el carácter \uFEFF
            int offset = 0;
            if (hasBom)
            {
                byte[] preamble = enc.GetPreamble();
                if (preamble != null && preamble.Length > 0 && bytes.Length >= preamble.Length) offset = preamble.Length;
            }
            string text = enc.GetString(bytes, offset, bytes.Length - offset);
            string newline = NewLineUtil.Detect(text);

            return new FileSnapshot
            {
                Path = path,
                OriginalBytes = bytes,
                Encoding = enc,
                HasBom = hasBom,
                Text = text,
                NewLine = newline,
                Sha256 = HashUtil.Sha256(bytes)
            };
        }

        public void WriteFull(FileSnapshot snap, string newText)
        {
            // Normalización obligatoria
            newText = newText.Normalize(NormalizationForm.FormC);

            // Validación representabilidad
            var safeEnc = Encoding.GetEncoding(
                snap.Encoding.CodePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback
            );

            byte[] newBytes = safeEnc.GetBytes(newText);

            File.WriteAllBytes(snap.Path, newBytes);
        }

        public void ApplyPatchOnly(FileSnapshot snap, string diffText, string expectedHash, bool allowLarge)
        {
            int maxTouchedLines = allowLarge ? 1000 : 200;
            if (snap.Sha256 != expectedHash) throw new InvalidOperationException("Archivo modificado externamente");

            var diff = UnifiedDiffParser.Parse(diffText);
            string baseText = NormalizeToLf(snap.Text);
            UnifiedDiffValidator.Validate(diff, baseText.Split('\n').Length, maxTouchedLines);
            UnifiedDiffSemanticValidator.ValidateAgainstText(diff, baseText);

            string patched = UnifiedDiffApplier.Apply(baseText, diff);

            FileSnapshotWriter.WritePatched(snap, patched);
        }

        static string NormalizeToLf(string text)
        {
            return text.Replace("\r\n", "\n").Replace("\r", "\n");
        }

    }
}