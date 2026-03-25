using McpHost.Diff;
using McpHost.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Text;
using System.Web.Script.Serialization;

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

                // Diff canónico con las posiciones originales del cliente. Se usa en el fallback
                // con patch.exe en caso de que el validador semántico lance, dado que el objeto
                // diff puede quedar en estado inconsistente si el validador falló a mitad.
                string canonicalDiffFallback = BuildCanonicalUnifiedDiff(diff);
                bool semanticValidationPassed = false;

                try
                {
                    UnifiedDiffSemanticValidator.ValidateAgainstText(diff, baseText);
                    semanticValidationPassed = true;
                }
                catch (InvalidOperationException ex)
                {
                    // Fallback: si patch.exe valida el diff completo, no bloquear por falso negativo
                    // del validador semántico interno.
                    try
                    {
                        ExternalPatchEngine.Validate(canonicalDiffFallback, baseText);
                    }
                    catch
                    {
                        throw new PatchException(
                            ex.Message,
                            errorCode: "patch_semantic_mismatch",
                            reason: ex.Message,
                            inner: ex);
                    }
                }

                // Si el validador semántico reubicó hunks, reconstruir con posiciones corregidas.
                // Si falló pero patch.exe aceptó el diff, usar posiciones originales.
                string canonicalDiff = semanticValidationPassed
                    ? BuildCanonicalUnifiedDiff(diff)
                    : canonicalDiffFallback;

                if (parseOnly)
                {
                    ExternalPatchEngine.Validate(canonicalDiff, baseText);
                    return;
                }

                string patched = ExternalPatchEngine.Apply(canonicalDiff, baseText);

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
                    ex.EvidenceDirectory,
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
                       "DIFF RECHAZADO: Comprobá que el formato utilizado sea unified diff compatible con patch.exe (Git for Windows).";
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

            if (message.StartsWith("patch.exe falló", StringComparison.OrdinalIgnoreCase))
            {
                return message + "\n\n" +
                       newlineNote + "\n" +
                       "Si el mensaje incluye 'Hunk #... FAILED', el problema suele ser contexto incorrecto en el diff (no solo offset de líneas).\n" +
                       "CLAUDE: usar parse_only=true para preflight y luego regenerar el patch con hunks más chicos y contexto exacto.";
            }

            return message;
        }

        static string NormalizeToLf(string text)
        {
            return text.Replace("\r\n", "\n").Replace("\r", "\n");
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
        public string EvidenceDirectory { get; private set; }

        public PatchException(
            string message,
            string errorCode = null,
            int? hunkIndex = null,
            int? diffLineNumber = null,
            string reason = null,
            string expectedFormat = null,
            string problematicLine = null,
            string evidenceDirectory = null,
            Exception inner = null)
            : base(message, inner)
        {
            ErrorCode = errorCode;
            HunkIndex = hunkIndex;
            DiffLineNumber = diffLineNumber;
            Reason = reason;
            ExpectedFormat = expectedFormat;
            EvidenceDirectory = evidenceDirectory;
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
            if (!string.IsNullOrEmpty(EvidenceDirectory)) data["evidence_directory"] = EvidenceDirectory;
            return data;
        }
    }

    static class McpErrorLogger
    {
        const long MaxLogSizeBytes = 100L * 1024L * 1024L;
        static readonly TimeSpan Retention = TimeSpan.FromDays(60);
        static readonly object Sync = new object();
        static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        static DateTime _lastCleanupUtc = DateTime.MinValue;

        static string BaseDir => AppDomain.CurrentDomain.BaseDirectory;
        static string IncidentsDir => Path.Combine(BaseDir, "erroresmcp");
        static string LogPath => Path.Combine(BaseDir, "erroresmcp.log");

        public static string CreateIncidentDirectory(string area)
        {
            lock (Sync)
            {
                EnsureMaintenanceLocked();
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                string id = Guid.NewGuid().ToString("N").Substring(0, 8);
                string safeArea = SanitizeFileName(string.IsNullOrWhiteSpace(area) ? "mcp" : area);
                string dir = Path.Combine(IncidentsDir, stamp + "_" + id + "_" + safeArea);
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string SaveTextFile(string incidentDir, string fileName, string content)
        {
            if (string.IsNullOrWhiteSpace(incidentDir)) return null;
            try
            {
                Directory.CreateDirectory(incidentDir);
                string path = Path.Combine(incidentDir, SanitizeFileName(fileName));
                File.WriteAllText(path, content ?? string.Empty, Utf8NoBom);
                return path;
            }
            catch
            {
                return null;
            }
        }

        public static string SaveBytesFile(string incidentDir, string fileName, byte[] content)
        {
            if (string.IsNullOrWhiteSpace(incidentDir)) return null;
            try
            {
                Directory.CreateDirectory(incidentDir);
                string path = Path.Combine(incidentDir, SanitizeFileName(fileName));
                File.WriteAllBytes(path, content ?? new byte[0]);
                return path;
            }
            catch
            {
                return null;
            }
        }

        public static void LogError(string stage, string toolName, string message, Exception ex, Dictionary<string, object> errorData, Dictionary<string, object> args, Dictionary<string, string> fileRefs, string root)
        {
            lock (Sync)
            {
                EnsureMaintenanceLocked();

                var row = new Dictionary<string, object>
                {
                    { "timestamp", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") },
                    { "stage", stage ?? "unknown" },
                    { "tool", toolName ?? "(none)" },
                    { "message", message ?? string.Empty },
                    { "error_data", errorData ?? new Dictionary<string, object>() },
                    { "arguments", args ?? new Dictionary<string, object>() },
                    { "root", root ?? string.Empty },
                    { "files", fileRefs ?? new Dictionary<string, string>() }
                };

                if (ex != null) row["exception"] = ex.ToString();

                string line = Json.Serialize(row) + Environment.NewLine;
                File.AppendAllText(LogPath, line, Utf8NoBom);
            }
        }

        static void EnsureMaintenanceLocked()
        {
            Directory.CreateDirectory(IncidentsDir);
            TruncateLogIfNeeded();

            DateTime now = DateTime.UtcNow;
            if ((now - _lastCleanupUtc) < TimeSpan.FromMinutes(15)) return;
            _lastCleanupUtc = now;

            foreach (string dir in Directory.EnumerateDirectories(IncidentsDir))
            {
                try
                {
                    var info = new DirectoryInfo(dir);
                    DateTime last = info.LastWriteTimeUtc;
                    if (last == DateTime.MinValue) last = info.CreationTimeUtc;
                    if (last != DateTime.MinValue && (now - last) > Retention)
                        info.Delete(true);
                }
                catch { }
            }
        }

        static void TruncateLogIfNeeded()
        {
            try
            {
                if (!File.Exists(LogPath)) return;
                var info = new FileInfo(LogPath);
                if (info.Length <= MaxLogSizeBytes) return;
                File.WriteAllText(LogPath, string.Empty, Utf8NoBom);
            }
            catch { }
        }

        static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "file";
            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return sb.ToString();
        }
    }
}
