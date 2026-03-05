using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

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
                // Escribir archivo temporal (LF, UTF-8 sin BOM)
                File.WriteAllText(fileTmp, normalizedLfContent, utf8NoBom);
                // Normalizar diff a LF (por si viene con CRLF desde el cliente)
                File.WriteAllText(diffTmp, NormalizeToLf(diffText), utf8NoBom);

                // Armar argumentos
                var argsList = new List<string>
                {
                    "--forward", "--batch", "--no-backup-if-mismatch"
                };
                if (dryRun) argsList.Add("--dry-run");
                argsList.Add("-i");
                argsList.Add(QuoteArg(diffTmp));
                argsList.Add(QuoteArg(fileTmp));

                string args = string.Join(" ", argsList);

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
                    throw new PatchException(
                        $"patch.exe falló (exit {exitCode}).\n{output}",
                        errorCode: "patch_apply_failed",
                        reason: output);
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

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
