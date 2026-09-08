using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace McpHost.Core
{
    static class ExternalPatchEngine
    {
        // Valida el diff contra el contenido sin escribir (dry-run). Lanza PatchException si falla.
        public static void Validate(string diffText, string normalizedLfContent)
            => RunCore(diffText, normalizedLfContent, dryRun: true);

        // Aplica el diff. Retorna el contenido parchado (LF, UTF-8). Lanza PatchException si falla.
        public static string Apply(string diffText, string normalizedLfContent)
            => RunCore(diffText, normalizedLfContent, dryRun: false);

        static string RunCore(string diffText, string normalizedLfContent, bool dryRun)
        {
            string patchExe = McpConfig.Instance.PatchExePath;
            string tempDir = Path.GetTempPath();
            string id = Guid.NewGuid().ToString("N");
            string fileTmp = Path.Combine(tempDir, id + ".tmp");
            string diffTmp = Path.Combine(tempDir, id + ".patch");
            var utf8NoBom = new UTF8Encoding(false);

            try
            {
                string args = null;
                // Escribir archivo temporal (LF, UTF-8 sin BOM)
                File.WriteAllText(fileTmp, normalizedLfContent, utf8NoBom);
                // Normalizar diff a LF (por si viene con CRLF desde el cliente)
                string normalizedDiff = NormalizeToLf(diffText);
                // patch.exe (Git for Windows) es sensible al EOF sin newline en ciertos hunks.
                if (!normalizedDiff.EndsWith("\n", StringComparison.Ordinal)) normalizedDiff += "\n";
                File.WriteAllText(diffTmp, normalizedDiff, utf8NoBom);

                // Armar argumentos
                var argsList = new List<string>
                {
                    "--forward", "--batch", "--no-backup-if-mismatch"
                };
                if (dryRun) argsList.Add("--dry-run");
                argsList.Add("-i");
                argsList.Add(QuoteArg(diffTmp));
                argsList.Add(QuoteArg(fileTmp));

                args = string.Join(" ", argsList);

                // Ejecutar patch.exe
                var psi = new ProcessStartInfo
                {
                    FileName = patchExe,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                string stdout, stderr;
                int exitCode;

                using (var proc = Process.Start(psi))
                {
                    stdout = proc.StandardOutput.ReadToEnd();
                    stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit();
                    exitCode = proc.ExitCode;
                }

                if (exitCode != 0)
                {
                    string output = (stdout + "\n" + stderr).Trim();
                    string hunkSummary = BuildHunkFailuresSummary(output);
                    string incidentDir = McpErrorLogger.CreateIncidentDirectory("patch_engine");
                    McpErrorLogger.SaveBytesFile(incidentDir, "archivo_original.tmp", File.ReadAllBytes(fileTmp));
                    McpErrorLogger.SaveTextFile(incidentDir, "archivo_temp_enviado_a_patch.tmp", File.ReadAllText(fileTmp, utf8NoBom));
                    McpErrorLogger.SaveTextFile(incidentDir, "diff_enviado_a_patch.diff", File.ReadAllText(diffTmp, utf8NoBom));
                    McpErrorLogger.SaveTextFile(
                        incidentDir,
                        "patch_stdout_stderr.txt",
                        "exit_code=" + exitCode + "\n" +
                        "patch_exe=" + patchExe + "\n" +
                        "args=" + (args ?? string.Empty) + "\n\n" +
                        output +
                        (string.IsNullOrWhiteSpace(hunkSummary) ? string.Empty : ("\n\n" + hunkSummary)));

                    if (!string.IsNullOrWhiteSpace(hunkSummary))
                        McpErrorLogger.SaveTextFile(incidentDir, "diagnostico_hunks.txt", hunkSummary);

                    if (File.Exists(fileTmp + ".rej"))
                        McpErrorLogger.SaveTextFile(incidentDir, "archivo_temp.rej", File.ReadAllText(fileTmp + ".rej", utf8NoBom));

                    string outputConDiagnostico = output + (string.IsNullOrWhiteSpace(hunkSummary) ? string.Empty : ("\n\n" + hunkSummary));

                    throw new PatchException(
                        $"patch.exe falló (exit {exitCode}).\n{outputConDiagnostico}",
                        errorCode: "patch_apply_failed",
                        reason: outputConDiagnostico,
                        evidenceDirectory: incidentDir);
                }

                if (dryRun) return null;

                return File.ReadAllText(fileTmp, utf8NoBom);
            }
            finally
            {
                TryDelete(fileTmp);
                TryDelete(diffTmp);
                TryDelete(fileTmp + ".rej");  // patch.exe puede crear <file>.rej en caso de fallos parciales
            }
        }

        static string NormalizeToLf(string text)
            => text.Replace("\r\n", "\n").Replace("\r", "\n");

        static string QuoteArg(string path)
            => "\"" + path.Replace("\"", "\\\"") + "\"";

        // Marcador que FileGateway usa para reconocer un diagnóstico decisivo de patch.exe.
        internal const string PatchAlreadyAppliedMarker = "el patch YA ESTÁ APLICADO";

        static string BuildHunkFailuresSummary(string patchOutput)
        {
            if (string.IsNullOrWhiteSpace(patchOutput)) return null;

            // patch.exe reconoció el diff pero el archivo ya tiene los cambios aplicados (o el diff
            // está invertido). Regenerar el mismo diff no sirve: hay que releer el archivo.
            if (patchOutput.IndexOf("Reversed (or previously applied)", StringComparison.OrdinalIgnoreCase) >= 0)
                return
                    "Diagnóstico automático:\n" +
                    "- patch.exe detectó que " + PatchAlreadyAppliedMarker + " en el archivo, o que el diff\n" +
                    "  está invertido ('+' y '-' al revés).\n" +
                    "- NO regeneres el mismo diff: re-leé el archivo (file_read_range) y fijate si el cambio ya está.\n" +
                    "- Si ya está aplicado no hay nada que hacer; si querés revertirlo, generá el diff en el sentido inverso.";

            // Los @@ deben ir por número de línea ascendente y sin solaparse.
            bool misordered = patchOutput.IndexOf("misordered hunks", StringComparison.OrdinalIgnoreCase) >= 0;

            var matches = Regex.Matches(
                patchOutput,
                @"Hunk\s+#(?<h>\d+)\s+FAILED\s+at\s+(?<l>\d+)\.",
                RegexOptions.IgnoreCase);

            var parts = new List<string>();
            foreach (Match m in matches)
            {
                string h = m.Groups["h"].Value;
                string l = m.Groups["l"].Value;
                if (!string.IsNullOrWhiteSpace(h) && !string.IsNullOrWhiteSpace(l))
                    parts.Add("hunk " + h + " (línea aprox. " + l + ")");
            }

            if (parts.Count == 0 && !misordered) return null;

            var sb = new StringBuilder("Diagnóstico automático:\n");
            if (misordered)
                sb.Append("- patch.exe rechazó el diff por HUNKS FUERA DE ORDEN ('misordered hunks'): los @@ deben ir por número de línea ASCENDENTE y sin solaparse.\n");
            if (parts.Count > 0)
                sb.Append("- patch.exe detectó fallos en " + parts.Count + " hunk(s): " + string.Join(", ", parts) + ".\n");
            if (!misordered)
                sb.Append("- Esto suele indicar que el contexto del diff no coincide exactamente con el archivo (no solo desplazamiento de línea).\n");
            sb.Append("- Recomendación: re-leer rangos exactos y regenerar el diff en hunks más pequeños, o validar primero con parse_only=true.");
            return sb.ToString();
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
