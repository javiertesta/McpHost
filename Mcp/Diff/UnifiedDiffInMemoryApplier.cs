using System.Collections.Generic;
using McpHost.Core;

namespace McpHost.Diff
{
    // Segundo motor, independiente de patch.exe: aplica el plan directamente sobre las líneas del archivo.
    // FileGateway sólo escribe si los dos motores dan exactamente el mismo resultado.
    static class UnifiedDiffInMemoryApplier
    {
        public static List<string> Apply(PatchPlan plan)
        {
            List<string> source = plan.RealLines;
            var result = new List<string>(source.Count);
            int position = 0;

            foreach (var hunk in plan.Hunks)
            {
                if (hunk.Start0 < position || hunk.Start0 > source.Count)
                    throw new PatchException(
                        "Error interno del MCP: el hunk " + hunk.Number + " quedó fuera de orden o de rango al aplicarlo en memoria. No se modificó el archivo.",
                        errorCode: "patch_plan_invalid",
                        hunkIndex: hunk.Number);

                for (int i = position; i < hunk.Start0; i++)
                    result.Add(source[i]);

                int idx = hunk.Start0;
                foreach (var line in hunk.Lines)
                {
                    char prefix = line[0];
                    string text = line.Substring(1);

                    if (prefix == '+')
                    {
                        result.Add(text);
                        continue;
                    }

                    if (idx >= source.Count || source[idx] != text)
                        throw new PatchException(
                            "Error interno del MCP: el hunk " + hunk.Number + " no coincide con el archivo en la línea " + (idx + 1) + " al aplicarlo en memoria. No se modificó el archivo.",
                            errorCode: "patch_plan_invalid",
                            hunkIndex: hunk.Number);

                    if (prefix == ' ') result.Add(text);
                    idx++;
                }

                position = idx;
            }

            for (int i = position; i < source.Count; i++)
                result.Add(source[i]);

            return result;
        }
    }
}
