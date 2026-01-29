using McpHost.Diff;
using McpHost.Utils;
using System;
using System.IO;
using System.Text;

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

            // Hash estricto (bytes originales) y hash tolerante (normaliza newlines + colapsa whitespace).
            string shaStrict = HashUtil.Sha256(bytes);
            string shaWs = HashUtil.Sha256(new UTF8Encoding(false).GetBytes(
                WhitespaceNormalizeUtil.NormalizeForLooseHash(text)
            ));

            return new FileSnapshot
            {
                Path = path,
                OriginalBytes = bytes,
                Encoding = enc,
                HasBom = hasBom,
                Text = text,
                NewLine = newline,
                Sha256 = shaStrict,
                Sha256NormalizedWhitespace = shaWs
            };
        }

        public void ApplyPatchOnly(FileSnapshot snap, string diffText, string expectedHash, bool allowLarge)
        {
            int maxTouchedLines = allowLarge ? 1000 : 200;

            bool strictHashOk = string.Equals(snap.Sha256, expectedHash, StringComparison.OrdinalIgnoreCase);
            bool wsHashOk = string.Equals(snap.Sha256NormalizedWhitespace, expectedHash, StringComparison.OrdinalIgnoreCase);

            if (!strictHashOk && !wsHashOk)
                throw new InvalidOperationException(
                    "Archivo modificado externamente (hash no coincide).\n" +
                    $"Hash esperado: {expectedHash}\n" +
                    $"Hash archivo:  {snap.Sha256} (strict) / {snap.Sha256NormalizedWhitespace} (whitespace-normalized)");

            // Si el hash coincide solo en modo "whitespace-normalized", seguimos igual (esto habilita diffs donde
            // Claude cambió tabs/espacios o espaciado). Se recomienda revisar el patch resultante.

            // Si el archivo ya contiene caracteres problemáticos (p.ej. U+FFFD), el motor
            // va a rechazar SIEMPRE el resultado final. Cortamos temprano con un error
            // más accionable (y explícito para Claude).
            if (UnicodeIssueUtil.ContainsInvalidUnicode(snap.Text))
                throw new InvalidOperationException(
                    UnicodeIssueUtil.BuildInvalidUnicodeError(
                        snap.Text,
                        "El archivo de entrada contiene caracteres Unicode inválidos (U+FFFD o U+FEFF)."
                    )
                );

            try
            {
                var diff = UnifiedDiffParser.Parse(diffText);

                string baseText = NormalizeToLf(snap.Text);

                UnifiedDiffValidator.Validate(diff, baseText.Split('\n').Length, maxTouchedLines);
                UnifiedDiffSemanticValidator.ValidateAgainstText(diff, baseText);

                string patched = UnifiedDiffApplier.Apply(baseText, diff);

                FileSnapshotWriter.WritePatched(snap, patched);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(AugmentPatchError(ex.Message));
            }
        }

        static string AugmentPatchError(string message)
        {
            if (string.IsNullOrEmpty(message)) return "Error aplicando patch.";

            // Nota: el MCP normaliza saltos de línea. Si alguien intenta convertir LF<->CRLF manualmente,
            // puede introducir caracteres extraños y empeorar el diff.
            const string newlineNote =
                "Nota: el MCP normaliza saltos de línea (LF/CRLF) internamente y re-escribe el archivo con su newline original; " +
                "no hace falta convertir el diff con unix2dos/sed/printf.";

            if (message.StartsWith("Diff inválido", StringComparison.OrdinalIgnoreCase) ||
                message.StartsWith("Diff sin hunks", StringComparison.OrdinalIgnoreCase) ||
                message.StartsWith("Hunk inválido", StringComparison.OrdinalIgnoreCase))
            {
                return message + "\n\n" +
                       newlineNote + "\n" +
                       "CLAUDE: regenerá el diff como *unified diff* (git-style) y asegurate de que cada línea del hunk empiece con ' ', '+' o '-'.";
            }

            if (message.StartsWith("Patch vacío", StringComparison.OrdinalIgnoreCase) ||
                message.StartsWith("Patch demasiado", StringComparison.OrdinalIgnoreCase))
            {
                return message + "\n\n" +
                       "CLAUDE: ajustá el diff (más chico y focalizado) o pedí confirmación para usar --large si realmente corresponde.";
            }

            if (message.StartsWith("Contexto del patch no coincide", StringComparison.OrdinalIgnoreCase) ||
                message.StartsWith("Línea a eliminar no coincide", StringComparison.OrdinalIgnoreCase))
            {
                return message + "\n\n" +
                       newlineNote + "\n" +
                       "Causas típicas: tabs vs espacios, líneas partidas en el diff (no cortar líneas largas), o archivo cambiado. " +
                       "CLAUDE: re-leé el bloque exacto y regenerá el diff preservando tabs y sin envolver líneas.";
            }

            return message;
        }

        static string NormalizeToLf(string text)
        {
            return text.Replace("\r\n", "\n").Replace("\r", "\n");
        }

    }
}