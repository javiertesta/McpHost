using McpHost.Diff;
using McpHost.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Text;

namespace McpHost.Core
{
    class FileGateway
    {
        public FileSnapshot Read(string path)
        {
            var readTask = Task.Run(() => File.ReadAllBytes(path));
            if (!readTask.Wait(10000))
                throw new InvalidOperationException("File read timed out after 10s: " + path);
            byte[] bytes = readTask.Result;

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

        public void ApplyPatchOnly(FileSnapshot snap, string diffText, string expectedHash, bool allowLarge, bool allowExtraLarge)
        {
            ApplyPatchCore(snap, diffText, expectedHash, allowLarge, allowExtraLarge, parseOnly: false);
        }

        public void ValidatePatchOnly(FileSnapshot snap, string diffText, string expectedHash, bool allowLarge, bool allowExtraLarge)
        {
            ApplyPatchCore(snap, diffText, expectedHash, allowLarge, allowExtraLarge, parseOnly: true);
        }

        void ApplyPatchCore(FileSnapshot snap, string diffText, string expectedHash, bool allowLarge, bool allowExtraLarge, bool parseOnly)
        {
            int maxTouchedLines = allowExtraLarge ? 5000 : (allowLarge ? 1000 : 200);

            bool strictHashOk = string.Equals(snap.Sha256, expectedHash, StringComparison.OrdinalIgnoreCase);
            bool wsHashOk = string.Equals(snap.Sha256NormalizedWhitespace, expectedHash, StringComparison.OrdinalIgnoreCase);

            if (!strictHashOk && !wsHashOk)
                throw new PatchException(
                    "Archivo modificado externamente (hash no coincide).\n" +
                    $"Hash esperado: {expectedHash}\n" +
                    $"Hash archivo:  {snap.Sha256} (strict) / {snap.Sha256NormalizedWhitespace} (whitespace-normalized)",
                    errorCode: "hash_mismatch",
                    reason: "El hash recibido no coincide con el estado actual del archivo.");

            // Si el hash coincide solo en modo "whitespace-normalized", seguimos igual (esto habilita diffs donde
            // Claude cambió tabs/espacios o espaciado). Se recomienda revisar el patch resultante.

            // Si el archivo ya contiene caracteres problemáticos (p.ej. U+FFFD), el motor
            // va a rechazar SIEMPRE el resultado final. Cortamos temprano con un error
            // más accionable (y explícito para Claude).
            if (UnicodeIssueUtil.ContainsInvalidUnicode(snap.Text))
                throw new PatchException(
                    UnicodeIssueUtil.BuildInvalidUnicodeError(
                        snap.Text,
                        "El archivo de entrada contiene caracteres Unicode inválidos (U+FFFD o U+FEFF)."
                    ),
                    errorCode: "invalid_unicode_in_input",
                    reason: "El archivo contiene caracteres inválidos antes de aplicar el patch.");

            try
            {
                var diff = UnifiedDiffParser.Parse(diffText);

                string baseText = NormalizeToLf(snap.Text);

                UnifiedDiffValidator.Validate(diff, baseText.Split('\n').Length, maxTouchedLines);

                try
                {
                    UnifiedDiffSemanticValidator.ValidateAgainstText(diff, baseText);
                }
                catch (InvalidOperationException ex)
                {
                    throw new PatchException(
                        ex.Message,
                        errorCode: "patch_semantic_mismatch",
                        reason: ex.Message,
                        inner: ex);
                }

                // Los hunks se aplican individualmente de forma transaccional.

                if (parseOnly)
                {
                    ApplyHunksTransactional(diff, baseText, parseOnly: true);
                    return;
                }

                string patched = ApplyHunksTransactional(diff, baseText, parseOnly: false);

                if (allowExtraLarge)
                    WriteTimestampedBackup(snap);

                FileSnapshotWriter.WritePatched(snap, patched);
            }
            catch (PatchException ex)
            {
                throw new PatchException(
                    AugmentPatchError(ex.Message),
                    ex.ErrorCode,
                    ex.HunkIndex,
                    ex.DiffLineNumber,
                    ex.Reason,
                    ex.ExpectedFormat,
                    ex.ProblematicLine,
                    ex);
            }
            catch (InvalidOperationException ex)
            {
                throw new PatchException(
                    AugmentPatchError(ex.Message),
                    errorCode: "patch_apply_failed",
                    reason: ex.Message,
                    inner: ex);
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
                       "DIFF RECHAZADO: Comprobá que el formato utilizado sea el admitido por patch.exe (el que viene incluído con VS Code)";
            }

            if (message.StartsWith("Patch vacío", StringComparison.OrdinalIgnoreCase) ||
                message.StartsWith("Patch demasiado", StringComparison.OrdinalIgnoreCase))
            {
                return message + "\n\n" +
                       "CLAUDE: ajustá el diff (más chico y focalizado) o pedí confirmación para usar --large/--extralarge si realmente corresponde.";
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

        static string ApplyHunksTransactional(UnifiedDiff diff, string baseText, bool parseOnly)
        {
            string workingText = baseText;
            var failures = new List<string>();

            for (int i = 0; i < diff.Hunks.Count; i++)
            {
                var hunk = diff.Hunks[i];
                string oneHunkDiff = BuildCanonicalUnifiedDiff(diff, hunk);

                try
                {
                    if (parseOnly)
                        ExternalPatchEngine.Validate(oneHunkDiff, workingText);
                    else
                        workingText = ExternalPatchEngine.Apply(oneHunkDiff, workingText);
                }
                catch (PatchException ex)
                {
                    failures.Add(BuildHunkFailureMessage(i + 1, ex.Message));
                }
                catch (InvalidOperationException ex)
                {
                    failures.Add(BuildHunkFailureMessage(i + 1, ex.Message));
                }
            }

            if (failures.Count > 0)
                throw new PatchException(
                    BuildTransactionalFailureMessage(failures, parseOnly),
                    errorCode: "patch_apply_failed_multi_hunk",
                    reason: "Fallaron uno o más hunks durante la aplicación transaccional.");

            return workingText;
        }


        static string BuildHunkFailureMessage(int hunkIndex, string message)
        {
            string msg = string.IsNullOrWhiteSpace(message) ? "Error desconocido." : message.Trim();
            return "Hunk " + hunkIndex + ": " + msg;
        }

        static string BuildTransactionalFailureMessage(List<string> failures, bool parseOnly)
        {
            var sb = new StringBuilder();
            sb.AppendLine(parseOnly
                ? "La validación transaccional por hunk detectó errores."
                : "La aplicación transaccional por hunk detectó errores. No se escribió ningún cambio en disco.");
            sb.AppendLine("Detalle de fallos:");
            foreach (var failure in failures) sb.AppendLine("- " + failure);
            return sb.ToString().TrimEnd();
        }


        static string BuildCanonicalUnifiedDiff(UnifiedDiff diff)
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(diff.OriginalFile))
                sb.AppendLine(diff.OriginalFile.TrimEnd('\r', '\n'));
            if (!string.IsNullOrWhiteSpace(diff.NewFile))
                sb.AppendLine(diff.NewFile.TrimEnd('\r', '\n'));

            foreach (var h in diff.Hunks)
            {
                sb.Append("@@ -")
                  .Append(h.StartOriginal).Append(",").Append(h.LengthOriginal)
                  .Append(" +")
                  .Append(h.StartNew).Append(",").Append(h.LengthNew)
                  .AppendLine(" @@");

                foreach (var line in h.Lines)
                    sb.AppendLine(line);
            }

            return sb.ToString();
        }

        static string BuildCanonicalUnifiedDiff(UnifiedDiff diff, DiffHunk hunk)
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(diff.OriginalFile))
                sb.AppendLine(diff.OriginalFile.TrimEnd('\r', '\n'));
            if (!string.IsNullOrWhiteSpace(diff.NewFile))
                sb.AppendLine(diff.NewFile.TrimEnd('\r', '\n'));

            sb.Append("@@ -")
              .Append(hunk.StartOriginal).Append(",").Append(hunk.LengthOriginal)
              .Append(" +")
              .Append(hunk.StartNew).Append(",").Append(hunk.LengthNew)
              .AppendLine(" @@");

            foreach (var line in hunk.Lines)
                sb.AppendLine(line);

            return sb.ToString();
        }


        static void WriteTimestampedBackup(FileSnapshot snap)
        {
            string dir = Path.GetDirectoryName(snap.Path) ?? ".";
            string name = Path.GetFileName(snap.Path);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            string backupPath = Path.Combine(dir, name + ".bak-" + stamp);
            for (int i = 1; File.Exists(backupPath); i++)
            {
                backupPath = Path.Combine(dir, name + ".bak-" + stamp + "-" + i);
            }

            // Backup byte-identico del archivo original (antes de escribir el patch).
            File.WriteAllBytes(backupPath, snap.OriginalBytes);
        }

    }

    class PatchException : InvalidOperationException
    {
        public string ErrorCode { get; private set; }
        public int? HunkIndex { get; private set; }
        public int? DiffLineNumber { get; private set; }
        public string Reason { get; private set; }
        public string ExpectedFormat { get; private set; }
        public string ProblematicLine { get; private set; }

        public PatchException(
            string message,
            string errorCode = null,
            int? hunkIndex = null,
            int? diffLineNumber = null,
            string reason = null,
            string expectedFormat = null,
            string problematicLine = null,
            Exception inner = null)
            : base(message, inner)
        {
            ErrorCode = errorCode;
            HunkIndex = hunkIndex;
            DiffLineNumber = diffLineNumber;
            Reason = reason;
            ExpectedFormat = expectedFormat;
            ProblematicLine = problematicLine;
        }

        public Dictionary<string, object> ToErrorData()
        {
            var data = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(ErrorCode)) data["error_code"] = ErrorCode;
            if (HunkIndex.HasValue) data["hunk_index"] = HunkIndex.Value;
            if (DiffLineNumber.HasValue) data["diff_line_number"] = DiffLineNumber.Value;
            if (!string.IsNullOrEmpty(Reason)) data["reason"] = Reason;
            if (!string.IsNullOrEmpty(ExpectedFormat)) data["expected_format"] = ExpectedFormat;
            if (!string.IsNullOrEmpty(ProblematicLine)) data["problematic_line"] = ProblematicLine;
            return data;
        }
    }
}
