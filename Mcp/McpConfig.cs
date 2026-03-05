using System;
using System.IO;


namespace McpHost.Core
{
    class McpConfig
    {
        public string PatchExePath { get; private set; }
        public string RgExePath { get; private set; }

        static McpConfig _instance;
        public static McpConfig Instance
            => _instance ?? throw new InvalidOperationException("McpConfig no fue cargado. Llamá a McpConfig.Load() al inicio.");

        public static void Load(string exeDir)
        {
            string patch = Path.Combine(exeDir, "patch.exe");
            if (!File.Exists(patch))
                throw new InvalidOperationException(
                    $"patch.exe no encontrado en: {patch}\n" +
                    "Copiá patch.exe de Git (usr/bin/patch.exe) junto a Mcp.exe.");

            string rg = Path.Combine(exeDir, "rg.exe");
            _instance = new McpConfig
            {
                PatchExePath = patch,
                RgExePath = File.Exists(rg) ? rg : null
            };
        }
    }
}
