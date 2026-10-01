using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using McpHost.Core;

namespace McpHost.Diff
{
    // Un hunk ya ubicado de forma exacta en el archivo, listo para aplicarse.
    class PlannedHunk
    {
        public int Number { get; set; }          // 1-based, en el orden del diff recibido
        public int DeclaredStart { get; set; }   // inicio que traía el @@ recibido
        public int Start0 { get; set; }          // 0-based: primera línea vieja, o punto de inserción si no tiene
        public int IgnoredLeadingContext { get; set; }
        public int IgnoredTrailingContext { get; set; }

        // Con prefijo ' ', '+' o '-'. Las de contexto y las borradas llevan el texto REAL del archivo.
        public List<string> Lines { get; } = new List<string>();

        public int OldCount { get { return Lines.Count(l => l[0] != '+'); } }
    }

    class PatchPlan
    {
        // Líneas reales del archivo (LF), sin la "línea vacía" que deja el salto de línea final.
        public List<string> RealLines { get; set; }
        public bool HasFinalNewline { get; set; }
        public bool WasEmpty { get; set; }
        // Si el resultado termina en salto de línea: lo del archivo (uno vacío pasa a tenerlo), salvo que un
        // marcador "\ No newline at end of file" de un solo lado pida otra cosa.
        public bool FinalNewline { get; set; }
        public List<PlannedHunk> Hunks { get; } = new List<PlannedHunk>();
        public List<string> Warnings { get; } = new List<string>();
    }

    // Convierte los hunks ya validados en un plan exacto y arma el diff que se le pasa a patch.exe.
    //
    // patch.exe (GNU patch) ancla al final del archivo todo hunk con más contexto arriba que abajo: con
    // --fuzz=0 lo rechaza aunque el contexto sea exacto, y con fuzz lo aplica ignorando contexto. Por eso,
    // antes de llamarlo, se completa el contexto de abajo con líneas reales del archivo (o se fusiona con el
    // hunk siguiente si no hay lugar). El contexto y las líneas borradas se copian tal cual están en el
    // archivo, así las diferencias de espacios finales que tolera el validador no hacen fallar a patch.exe.
    static class UnifiedDiffNormalizer
    {
        public static PatchPlan BuildPlan(UnifiedDiff diff, string lfText)
        {
            // Misma vista que el validador: Split('\n'). Si el archivo termina en salto de línea, el último
            // elemento es "" y no es una línea real (file.read_range la muestra como una línea vacía final).
            string[] fileLines = lfText.Split('\n');
            var plan = new PatchPlan
            {
                WasEmpty = lfText.Length == 0,
                HasFinalNewline = lfText.EndsWith("\n", StringComparison.Ordinal)
            };
            plan.FinalNewline = plan.WasEmpty || plan.HasFinalNewline;
            int realCount = plan.WasEmpty ? 0 : (plan.HasFinalNewline ? fileLines.Length - 1 : fileLines.Length);
            plan.RealLines = new List<string>(fileLines.Take(realCount));

            int previousEnd = 0;
            for (int k = 0; k < diff.Hunks.Count; k++)
            {
                DiffHunk hunk = diff.Hunks[k];
                int number = k + 1;

                int start0;
                if (hunk.Lines.Any(l => l[0] != '+'))
                {
                    start0 = hunk.StartOriginal - 1;   // posición validada contra el contenido
                }
                else
                {
                    // Inserción pura sin contexto: sólo el número de línea la ubica. "@@ -N,0" = después de la línea N.
                    if (!hunk.HasNumericHeader)
                        throw new PatchException(
                            "Hunk " + number + ": es una inserción sin contexto y sin número de línea; no hay forma de ubicarla.",
                            errorCode: "unanchored_insertion",
                            hunkIndex: number,
                            expectedFormat: "Agregá al menos una línea de contexto, o un @@ con número de línea (\"@@ -N,0 +N+1,M @@\" inserta después de la línea N).");
                    start0 = hunk.DeclaredStartOriginal;
                }

                if (start0 < previousEnd)
                    throw new PatchException(
                        "Hunk " + number + ": se superpone con el hunk anterior o está fuera de orden.",
                        errorCode: "overlapping_hunks",
                        hunkIndex: number,
                        expectedFormat: "Los hunks deben ir por número de línea ascendente y sin superponerse.");
                if (start0 > realCount)
                    throw new PatchException(
                        "Hunk " + number + ": empieza después del final del archivo (línea " + start0 + " de " + realCount + ").",
                        errorCode: "hunk_out_of_range",
                        hunkIndex: number);

                var planned = new PlannedHunk
                {
                    Number = number,
                    DeclaredStart = hunk.DeclaredStartOriginal,
                    Start0 = start0,
                    IgnoredLeadingContext = hunk.IgnoredLeadingContext,
                    IgnoredTrailingContext = hunk.IgnoredTrailingContext
                };

                int idx = start0;
                foreach (var line in hunk.Lines)
                {
                    char prefix = line[0];
                    if (prefix == '+')
                    {
                        planned.Lines.Add(line);
                        continue;
                    }

                    if (idx >= realCount)
                    {
                        // Tras validar, sólo puede ser la "línea vacía" del salto final (o la única de un archivo
                        // vacío). No es una línea real: como contexto no aporta nada y borrarla no cambia el archivo.
                        if ((plan.HasFinalNewline || plan.WasEmpty) && idx == realCount)
                        {
                            if (prefix == '-')
                                plan.Warnings.Add("hunk " + number + ": se ignoró el borrado de la línea vacía final; no es una línea real sino el salto de línea con que termina el archivo.");
                            idx++;
                            continue;
                        }

                        throw new PatchException(
                            "Hunk " + number + ": pasa del final del archivo.",
                            errorCode: "hunk_out_of_range",
                            hunkIndex: number);
                    }

                    planned.Lines.Add(prefix + plan.RealLines[idx]);
                    idx++;
                }

                // Marcador "\ No newline at end of file" (el parser garantiza que sólo viene en el último hunk). Con
                // un solo lado marcado, el diff pide agregar o sacar el salto de línea final y se respeta. Sin
                // marcador, o con los dos lados marcados (no cambia), se conserva el del archivo.
                if (hunk.OldNoNewlineAtEnd || hunk.NewNoNewlineAtEnd)
                {
                    if (idx < realCount)
                        throw new PatchException(
                            "Hunk " + number + ": trae el marcador '\\ No newline at end of file', pero no llega al final del archivo (termina en la línea " + idx + " de " + realCount + ").",
                            errorCode: "eof_marker_not_at_end",
                            hunkIndex: number,
                            expectedFormat: "El marcador sólo va en un hunk que termina en la última línea del archivo. Si no querés cambiar el salto de línea final, sacalo.");

                    if (hunk.OldNoNewlineAtEnd != hunk.NewNoNewlineAtEnd)
                    {
                        bool pedido = hunk.OldNoNewlineAtEnd;   // sólo el viejo no tenía salto final: el nuevo sí
                        if (pedido != plan.FinalNewline)
                            plan.Warnings.Add(pedido
                                ? "el archivo queda terminado en salto de línea, como pide el marcador '\\ No newline at end of file' del diff."
                                : "el archivo queda sin salto de línea al final, como pide el marcador '\\ No newline at end of file' del diff.");
                        plan.FinalNewline = pedido;
                    }
                }

                // Si sólo "borraba" la línea vacía final, no queda nada por hacer.
                if (!planned.Lines.Any(l => l[0] != ' ')) continue;

                plan.Hunks.Add(planned);
                previousEnd = start0 + planned.OldCount;
            }

            return plan;
        }

        // Diff para patch.exe: encabezados sintéticos y, en cada hunk, contexto de abajo completado hasta igualar
        // al de arriba. Un hunk que llega al final del archivo puede quedar asimétrico: ahí el anclaje es correcto.
        public static string BuildPatchExeDiff(PatchPlan plan)
        {
            var blocks = new List<PlannedHunk>();
            int i = 0;
            while (i < plan.Hunks.Count)
            {
                var block = new PlannedHunk { Start0 = plan.Hunks[i].Start0 };
                block.Lines.AddRange(plan.Hunks[i].Lines);
                int next = i + 1;

                while (true)
                {
                    int missing = LeadingContext(block.Lines) - TrailingContext(block.Lines);
                    if (missing <= 0) break;

                    int end = block.Start0 + block.OldCount;
                    int limit = next < plan.Hunks.Count ? plan.Hunks[next].Start0 : plan.RealLines.Count;
                    int take = Math.Min(missing, limit - end);
                    for (int j = 0; j < take; j++)
                        block.Lines.Add(" " + plan.RealLines[end + j]);
                    if (take == missing) break;

                    // No hay lugar antes del hunk siguiente: se fusionan y lo del medio queda como contexto interno.
                    if (next < plan.Hunks.Count)
                    {
                        block.Lines.AddRange(plan.Hunks[next].Lines);
                        next++;
                        continue;
                    }

                    break;
                }

                blocks.Add(block);
                i = next;
            }

            var sb = new StringBuilder("--- a/file\n+++ b/file\n");
            int delta = 0;
            foreach (var block in blocks)
            {
                int oldCount = block.OldCount;
                int newCount = block.Lines.Count(l => l[0] != '-');
                // Con 0 líneas, el número es la línea DESPUÉS de la cual va el cambio ("@@ -N,0"); si no, la primera.
                int oldStart = oldCount > 0 ? block.Start0 + 1 : block.Start0;
                int newStart = newCount > 0 ? block.Start0 + delta + 1 : block.Start0 + delta;

                sb.Append("@@ -").Append(oldStart).Append(',').Append(oldCount)
                  .Append(" +").Append(newStart).Append(',').Append(newCount).Append(" @@\n");
                foreach (var line in block.Lines)
                    sb.Append(line).Append('\n');

                delta += newCount - oldCount;
            }

            return sb.ToString();
        }

        // Texto que se le pasa a patch.exe: siempre con salto final, así la última línea se compara igual que las
        // demás. Si el archivo terminaba o no en salto de línea lo reconstruye ComposeText.
        public static string BuildPatchExeInput(PatchPlan plan)
        {
            if (plan.RealLines.Count == 0) return string.Empty;
            return string.Join("\n", plan.RealLines) + "\n";
        }

        public static List<string> SplitPatchExeOutput(string text)
        {
            if (string.IsNullOrEmpty(text)) return new List<string>();
            string body = text.EndsWith("\n", StringComparison.Ordinal) ? text.Substring(0, text.Length - 1) : text;
            return new List<string>(body.Split('\n'));
        }

        // Texto final (LF). Termina o no en salto de línea según plan.FinalNewline: lo del archivo, salvo que un
        // marcador "\ No newline at end of file" pida otra cosa.
        public static string ComposeText(PatchPlan plan, List<string> lines)
        {
            if (lines.Count == 0) return string.Empty;
            return string.Join("\n", lines) + (plan.FinalNewline ? "\n" : string.Empty);
        }

        static int LeadingContext(List<string> lines)
        {
            int n = 0;
            while (n < lines.Count && lines[n][0] == ' ') n++;
            return n;
        }

        static int TrailingContext(List<string> lines)
        {
            int n = 0;
            while (n < lines.Count && lines[lines.Count - 1 - n][0] == ' ') n++;
            return n;
        }
    }
}
