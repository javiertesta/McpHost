using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;


namespace McpHost.Core
{
    // Configuración del MCP: mcp.config.json, en la misma carpeta que Mcp.exe. Por ahora define de dónde sale
    // patch.exe, el de Git for Windows que aplica los diffs:
    //   { "patchExecutablePath": "C:\\Program Files\\Git\\usr\\bin\\patch.exe" }
    // La ruta puede ser absoluta o relativa a la carpeta de Mcp.exe ("patch.exe" = una copia al lado del exe).
    // Sin fallback: si falta el archivo o la clave, si el JSON está mal escrito o trae una clave desconocida, o
    // si la ruta no existe, el MCP no arranca y dice por qué.
    class McpConfig
    {
        public const string FileName = "mcp.config.json";
        const string PatchKey = "patchExecutablePath";

        public string PatchExePath { get; private set; }
        public string RgExePath { get; private set; }

        static McpConfig _instance;
        public static McpConfig Instance
            => _instance ?? throw new InvalidOperationException("McpConfig no fue cargado. Llamá a McpConfig.Load() al inicio.");

        public static void Load(string exeDir)
        {
            string configPath = Path.Combine(exeDir, FileName);
            if (!File.Exists(configPath))
                throw new InvalidOperationException(
                    $"No se encontró {FileName} junto a Mcp.exe ({configPath}).\n" +
                    $"Tiene que indicar de dónde sale patch.exe, por ejemplo: {{ \"{PatchKey}\": \"C:\\\\Program Files\\\\Git\\\\usr\\\\bin\\\\patch.exe\" }}");

            Dictionary<string, object> values;
            try
            {
                values = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(configPath));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"{FileName} no es un JSON válido ({configPath}): {ex.Message}");
            }
            if (values == null) values = new Dictionary<string, object>();

            foreach (string key in values.Keys)
                if (key != PatchKey)
                    throw new InvalidOperationException($"{FileName} trae una clave desconocida, \"{key}\" ({configPath}). La única que se usa es \"{PatchKey}\".");

            object raw;
            string configured = values.TryGetValue(PatchKey, out raw) ? raw as string : null;
            if (string.IsNullOrWhiteSpace(configured))
                throw new InvalidOperationException($"{FileName} no tiene \"{PatchKey}\" con la ruta de patch.exe, como texto ({configPath}).");

            string patch;
            try
            {
                patch = Path.GetFullPath(Path.Combine(exeDir, configured.Trim()));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                throw new InvalidOperationException($"\"{PatchKey}\" no es una ruta válida en {configPath}: {configured} ({ex.Message})");
            }
            if (!File.Exists(patch))
                throw new InvalidOperationException(
                    $"patch.exe no encontrado en: {patch}\n" +
                    $"Es la ruta de \"{PatchKey}\" en {configPath}.");

            string rg = Path.Combine(exeDir, "rg.exe");
            _instance = new McpConfig
            {
                PatchExePath = patch,
                RgExePath = File.Exists(rg) ? rg : null
            };
        }
    }
}
