using McpHost.Diff;
using McpHost.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
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
            try
            {
                if (!readTask.Wait(10000))
                    throw new InvalidOperationException("File read timed out after 10s: " + path);
            }
            catch (AggregateException ex)
            {
                // Task.Wait envuelve la excepción real (archivo inexistente, acceso denegado...) en una
                // AggregateException cuyo mensaje ("Se han producido uno o varios errores.") no dice nada.
                ExceptionDispatchInfo.Capture(ex.GetBaseException()).Throw();
                throw;
            }
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

        public PatchResult ApplyPatchOnly(FileSnapshot snap, string diffText, string expectedHash, bool allowLarge, bool allowExtraLarge)
        {
            return ApplyPatchCore(snap, diffText, expectedHash, allowLarge, allowExtraLarge, parseOnly: false);
        }

        public PatchResult ValidatePatchOnly(FileSnapshot snap, string diffText, string expectedHash, bool allowLarge, bool allowExtraLarge)
        {
            return ApplyPatchCore(snap, diffText, expectedHash, allowLarge, allowExtraLarge, parseOnly: true);
        }

        PatchResult ApplyPatchCore(FileSnapshot snap, string diffText, string expectedHash, bool allowLarge, bool allowExtraLarge, bool parseOnly)
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
                catch (InvalidOperationException ex) when (!(ex is PatchException))
                {
                    // Sin fallback a patch.exe: si el validador no ubica un hunk de forma exacta y única, patch.exe
                    // tampoco tiene que aplicarlo "donde le parezca" (con fuzz elegía el match más cercano aunque
                    // hubiera varios). Lo que sí se diagnostica es si el cambio ya estaba aplicado.
                    if (UnifiedDiffSemanticValidator.LooksAlreadyApplied(diffText, baseText))
                        throw AlreadyApplied(ex.Message, ex);

                    throw new PatchException(ex.Message, errorCode: "patch_semantic_mismatch", reason: ex.Message, inner: ex);
                }

                // Si para ubicar algún hunk hubo que ignorar contexto, antes de aplicar se descarta que el patch ya
                // estuviera aplicado: con contexto ignorado, un diff reenviado puede volver a "entrar" y duplicar líneas.
                if (diff.Hunks.Any(h => h.IgnoredLeadingContext > 0 || h.IgnoredTrailingContext > 0) &&
                    UnifiedDiffSemanticValidator.LooksAlreadyApplied(diffText, baseText))
                    throw AlreadyApplied("Para ubicar el diff hubo que ignorar contexto, y el contenido nuevo ya está en el archivo tal cual.", null);

                PatchPlan plan = UnifiedDiffNormalizer.BuildPlan(diff, baseText);
                string finalText = ApplyWithBothEngines(plan);

                // También con parse_only: así detecta caracteres no representables en el encoding original.
                byte[] finalBytes = FileSnapshotWriter.PrepareBytes(snap, finalText);

                PatchResult result = PatchResult.FromPlan(plan);
                if (parseOnly)
                {
                    // Nada cambió: los hashes son los del archivo actual, los que sirven para aplicar.
                    result.HashStrict = snap.Sha256;
                    result.HashNormalized = snap.Sha256NormalizedWhitespace;
                    result.TotalLines = baseText.Split('\n').Length;
                    return result;
                }

                if (allowExtraLarge)
                    result.BackupPath = WriteTimestampedBackup(snap);

                string writtenHash = FileSnapshotWriter.WriteBytes(snap, finalBytes);
                result.HashStrict = writtenHash;
                result.HashNormalized = HashUtil.Sha256(new UTF8Encoding(false).GetBytes(WhitespaceNormalizeUtil.NormalizeForLooseHash(finalText)));
                result.TotalLines = finalText.Split('\n').Length;

                // Se relee para verificar la escritura y devolver exactamente los hashes que daría un file.read.
                try
                {
                    FileSnapshot after = Read(snap.Path);
                    result.HashStrict = after.Sha256;
                    result.HashNormalized = after.Sha256NormalizedWhitespace;
                    if (!string.Equals(after.Sha256, writtenHash, StringComparison.OrdinalIgnoreCase))
                        result.Warnings.Add("el archivo cambió justo después de escribirlo (¿otro proceso?): releelo antes del próximo patch.");
                }
                catch (Exception ex)
                {
                    result.Warnings.Add("el patch se escribió, pero no se pudo releer el archivo para verificarlo: " + ex.Message);
                }

                return result;
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

        static PatchException AlreadyApplied(string detail, Exception inner)
        {
            return new PatchException(
                "El patch parece YA APLICADO: el contenido nuevo (líneas '+') ya está en el archivo.\n" +
                "NO reenvíes el mismo diff: re-leé el bloque (file_read_range) y confirmá. Si querés revertirlo, mandá el diff invertido.\n\n" +
                "=== Detalle del validador ===\n" + detail,
                errorCode: "patch_already_applied",
                reason: detail,
                inner: inner);
        }

        // patch.exe y la aplicación en memoria tienen que dar exactamente lo mismo; si no, no se escribe nada.
        static string ApplyWithBothEngines(PatchPlan plan)
        {
            // Sin hunks no hay nada que aplicar (pasa si el único cambio era borrar la línea vacía final, que se
            // ignora con un AVISO), y patch.exe rechaza un diff vacío.
            if (plan.Hunks.Count == 0)
                return UnifiedDiffNormalizer.ComposeText(plan, plan.RealLines);

            string patchExeDiff = UnifiedDiffNormalizer.BuildPatchExeDiff(plan);
            string patchExeInput = UnifiedDiffNormalizer.BuildPatchExeInput(plan);

            List<string> fromPatchExe = UnifiedDiffNormalizer.SplitPatchExeOutput(ExternalPatchEngine.Apply(patchExeDiff, patchExeInput));
            List<string> fromMemory = UnifiedDiffInMemoryApplier.Apply(plan);

            int difference = FirstDifference(fromPatchExe, fromMemory);
            if (difference >= 0)
            {
                string evidence = SaveEngineMismatchEvidence(plan, patchExeDiff, patchExeInput, fromPatchExe, fromMemory);
                throw new PatchException(
                    "Error interno del MCP: patch.exe y la aplicación en memoria dieron resultados distintos " +
                    "(primera diferencia en la línea " + (difference + 1) + " del resultado). No se modificó el archivo." +
                    (evidence == null ? string.Empty : "\nEvidencia: " + evidence),
                    errorCode: "patch_engine_mismatch",
                    evidenceDirectory: evidence);
            }

            return UnifiedDiffNormalizer.ComposeText(plan, fromMemory);
        }

        static int FirstDifference(List<string> a, List<string> b)
        {
            int common = Math.Min(a.Count, b.Count);
            for (int i = 0; i < common; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return i;
            return a.Count == b.Count ? -1 : common;
        }

        static string SaveEngineMismatchEvidence(PatchPlan plan, string patchExeDiff, string patchExeInput, List<string> fromPatchExe, List<string> fromMemory)
        {
            string dir = McpErrorLogger.CreateIncidentDirectory("patch_engine_mismatch");
            if (dir == null) return null;

            var planText = new StringBuilder();
            foreach (var hunk in plan.Hunks)
                planText.Append("hunk ").Append(hunk.Number)
                        .Append(": declarado ").Append(hunk.DeclaredStart)
                        .Append(", start0 ").Append(hunk.Start0)
                        .Append(", lineas ").Append(hunk.Lines.Count).Append('\n');

            McpErrorLogger.SaveTextFile(dir, "entrada_patch_exe.txt", patchExeInput);
            McpErrorLogger.SaveTextFile(dir, "diff_normalizado.diff", patchExeDiff);
            McpErrorLogger.SaveTextFile(dir, "resultado_patch_exe.txt", string.Join("\n", fromPatchExe));
            McpErrorLogger.SaveTextFile(dir, "resultado_en_memoria.txt", string.Join("\n", fromMemory));
            McpErrorLogger.SaveTextFile(dir, "plan.txt", planText.ToString());
            return dir;
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
                // Con el diff ya validado y normalizado, que falle patch.exe no es culpa del diff recibido.
                return message + "\n\n" +
                       "Nota: el MCP ya había ubicado y validado todos los hunks antes de llamar a patch.exe; esto es un problema del MCP, no del diff. No se modificó el archivo.\n" +
                       "CLAUDE: avisale al usuario (queda evidencia en erroresmcp) y, mientras tanto, probá partiendo el cambio en llamadas separadas.";
            }

            return message;
        }

        static string NormalizeToLf(string text)
        {
            return text.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        static string WriteTimestampedBackup(FileSnapshot snap)
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
            return backupPath;
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
            try
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
            catch (Exception ex)
            {
                // El logger nunca debe tapar el error original ni tumbar el servidor.
                ReportLoggerFailure("CreateIncidentDirectory", ex);
                return null;
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
            try
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
            catch (Exception logEx)
            {
                // Si el log está bloqueado (otra instancia, antivirus, backup), se pierde esta línea, no el servidor.
                ReportLoggerFailure("LogError", logEx);
            }
        }

        static void ReportLoggerFailure(string where, Exception ex)
        {
            try { Console.Error.WriteLine("[erroresmcp] " + where + " falló: " + ex.Message); }
            catch { }
        }

        // Mantenimiento "best effort": si falla (permisos, carpeta bloqueada), se sigue logueando igual.
        static void EnsureMaintenanceLocked()
        {
            try
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
            catch { }
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
