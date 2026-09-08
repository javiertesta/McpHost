using System;
using System.Collections.Generic;
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

            // 2.1) Clonar encoding en modo estricto: sin reemplazos silenciosos por '?'
            Encoding strictEncoding = CloneWithStrictFallback(snapshot.Encoding);

            // 3) Texto -> bytes con el encoding original (sin BOM todavía)
            byte[] payloadBytes;
            try
            {
                payloadBytes = strictEncoding.GetBytes(finalText);
            }
            catch (EncoderFallbackException ex)
            {
                throw new InvalidOperationException(
                    "El texto contiene caracteres no representables en el encoding original del archivo ('" + snapshot.Encoding.WebName + "'). " +
                    "Se aborta para evitar reemplazos silenciosos por '?'." +
                    DescribeUnencodableChars(finalText, strictEncoding),
                    ex);
            }

            // 4) Reinsertar BOM si el archivo original lo tenía
            byte[] finalBytes = snapshot.HasBom
                ? PrependPreamble(strictEncoding, payloadBytes)
                : payloadBytes;

            // 5) Roundtrip check: bytes -> texto (saltando BOM si corresponde) y comparar
            string decoded = DecodeBytesRespectingBom(strictEncoding, finalBytes, snapshot.HasBom);
            if (!StringEqualsOrdinal(decoded, finalText))
                throw new InvalidOperationException(
                    "Roundtrip falló: el texto no puede ser re-codificado sin pérdida con el encoding original."
                );

            // 6) Escritura "lo más atómica posible"
            WriteAllBytesAtomic(snapshot.Path, finalBytes);

            // 7) Hash del resultado (sobre bytes finales)
            return ComputeSha256(finalBytes);
        }

        static Encoding CloneWithStrictFallback(Encoding enc)
        {
            if (enc == null) throw new ArgumentNullException("enc");

            var strict = (Encoding)enc.Clone();
            strict.EncoderFallback = EncoderFallback.ExceptionFallback;
            strict.DecoderFallback = DecoderFallback.ExceptionFallback;

            return strict;
        }

        // ---------------- Helpers ----------------

        // Detalla los caracteres que el encoding original no puede representar (codepoint, línea del
        // archivo YA parchado y contexto). Sin esto el modelo tiene que adivinar cuál insertó de más.
        // Solo corre en el camino de error, así que el costo del escaneo no importa.
        static string DescribeUnencodableChars(string finalText, Encoding strictEncoding)
        {
            const int maxReported = 10;
            if (String.IsNullOrEmpty(finalText) || strictEncoding == null) return String.Empty;

            var cache = new Dictionary<string, bool>(StringComparer.Ordinal);
            var detalles = new List<string>();
            int total = 0;

            string[] lines = NormalizeToLf(finalText).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                for (int j = 0; j < line.Length; j++)
                {
                    char c = line[j];
                    if (c < 0x80) continue;   // ASCII: siempre representable

                    bool esPar = Char.IsHighSurrogate(c) && j + 1 < line.Length && Char.IsLowSurrogate(line[j + 1]);
                    string unit = esPar ? line.Substring(j, 2) : c.ToString();
                    if (esPar) j++;

                    bool ok;
                    if (!cache.TryGetValue(unit, out ok))
                    {
                        ok = CanEncode(strictEncoding, unit);
                        cache[unit] = ok;
                    }
                    if (ok) continue;

                    total++;
                    if (detalles.Count >= maxReported) continue;

                    int cp = esPar ? Char.ConvertToUtf32(unit, 0) : (int)c;
                    // Un surrogate suelto rompería el JSON de salida: no lo mostramos literal.
                    string visible = (!esPar && Char.IsSurrogate(c)) ? "?" : unit;
                    detalles.Add("- U+" + cp.ToString("X4") + " '" + visible + "' en línea " + (i + 1) + ": " + Snippet(line));
                }
            }

            if (total == 0) return String.Empty;

            var sb = new StringBuilder();
            sb.Append("\n\nCaracteres no representables (").Append(total).Append("):\n");
            sb.Append(String.Join("\n", detalles.ToArray()));
            if (total > detalles.Count) sb.Append("\n- ... y ").Append(total - detalles.Count).Append(" más.");
            sb.Append("\n\nCLAUDE: reemplazá esos caracteres por equivalentes ASCII y reintentá ");
            sb.Append("(flechas -> \"->\" / \"<->\", comillas tipográficas -> \" o ', puntos suspensivos -> \"...\"). ");
            sb.Append("Los números de línea son los del archivo YA parchado. ");
            sb.Append("NO cambies el encoding del archivo: se preserva a propósito.");
            return sb.ToString();
        }

        static string Snippet(string line)
        {
            if (line == null) return String.Empty;
            string s = line.Trim();
            return s.Length <= 120 ? s : s.Substring(0, 120) + "...";
        }

        static bool CanEncode(Encoding strictEncoding, string s)
        {
            try
            {
                strictEncoding.GetBytes(s);
                return true;
            }
            catch (EncoderFallbackException)
            {
                return false;
            }
        }

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
