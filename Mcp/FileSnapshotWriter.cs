using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace McpHost.Core
{
    static class FileSnapshotWriter
    {
        /// <summary>
        /// Escribe el texto final preservando Encoding/BOM/NewLine del snapshot.
        /// Requiere que patchedText venga SIN BOM (texto "lógico") y preferentemente normalizado a LF.
        /// </summary>
        public static string WritePatched(FileSnapshot snapshot, string patchedText)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            if (patchedText == null) throw new ArgumentNullException("patchedText");

            // 1) Normalización defensiva: el motor trabaja interno en LF
            string lfText = NormalizeToLf(patchedText);

            // 2) Volver a newline original del archivo
            string finalText = ConvertLfToNewline(lfText, snapshot.NewLine);

            // 3) Texto -> bytes con el encoding original (sin BOM todavía)
            byte[] payloadBytes = snapshot.Encoding.GetBytes(finalText);

            // 4) Reinsertar BOM si el archivo original lo tenía
            byte[] finalBytes = snapshot.HasBom
                ? PrependPreamble(snapshot.Encoding, payloadBytes)
                : payloadBytes;

            // 5) Roundtrip check: bytes -> texto (saltando BOM si corresponde) y comparar
            string decoded = DecodeBytesRespectingBom(snapshot.Encoding, finalBytes, snapshot.HasBom);
            if (!StringEqualsOrdinal(decoded, finalText))
                throw new InvalidOperationException(
                    "Roundtrip falló: el texto no puede ser re-codificado sin pérdida con el encoding original."
                );

            // 6) Escritura "lo más atómica posible"
            WriteAllBytesAtomic(snapshot.Path, finalBytes);

            // 7) Hash del resultado (sobre bytes finales)
            return ComputeSha256(finalBytes);
        }

        // ---------------- Helpers ----------------

        static string NormalizeToLf(string text)
        {
            // Convertir todo a \n internamente
            return text.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        static string ConvertLfToNewline(string lfText, string newline)
        {
            if (String.IsNullOrEmpty(newline)) newline = Environment.NewLine;
            if (newline == "\n") return lfText;
            return lfText.Replace("\n", newline);
        }

        static byte[] PrependPreamble(Encoding enc, byte[] payload)
        {
            byte[] pre = enc.GetPreamble();
            if (pre == null || pre.Length == 0) return payload;

            byte[] all = new byte[pre.Length + payload.Length];
            Buffer.BlockCopy(pre, 0, all, 0, pre.Length);
            Buffer.BlockCopy(payload, 0, all, pre.Length, payload.Length);
            return all;
        }

        static string DecodeBytesRespectingBom(Encoding enc, byte[] bytes, bool hasBom)
        {
            int offset = 0;
            if (hasBom)
            {
                byte[] pre = enc.GetPreamble();
                if (pre != null && pre.Length > 0 && bytes.Length >= pre.Length)
                    offset = pre.Length;
            }

            return enc.GetString(bytes, offset, bytes.Length - offset);
        }

        static bool StringEqualsOrdinal(string a, string b)
        {
            return String.Equals(a, b, StringComparison.Ordinal);
        }

        static void WriteAllBytesAtomic(string path, byte[] bytes)
        {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentException("Path inválido", "path");

            string dir = Path.GetDirectoryName(path);
            if (String.IsNullOrEmpty(dir)) dir = ".";
            string tmp = Path.Combine(dir, Path.GetFileName(path) + ".tmp." + Guid.NewGuid().ToString("N"));
            string bak = Path.Combine(dir, Path.GetFileName(path) + ".bak." + Guid.NewGuid().ToString("N"));

            File.WriteAllBytes(tmp, bytes);

            try
            {
                // En Windows/.NET Framework suele funcionar bien:
                // reemplaza target por tmp y crea backup
                if (File.Exists(path))
                {
                    File.Replace(tmp, path, bak, true);
                    TryDelete(bak);
                }
                else
                {
                    File.Move(tmp, path);
                }
            }
            catch
            {
                // Fallback (por compatibilidad en Mono/WSL):
                // no es tan atómico como Replace, pero es robusto.
                try
                {
                    if (File.Exists(path))
                    {
                        TryDelete(path);
                    }
                    File.Move(tmp, path);
                }
                catch
                {
                    // Si falla el move, dejamos el tmp para inspección
                    throw;
                }
            }
            finally
            {
                // Si Replace tuvo éxito, el tmp ya no existe.
                TryDelete(tmp);
            }
        }

        static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        static string ComputeSha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                return BitConverter.ToString(hash).Replace("-", "");
            }
        }
    }
}
