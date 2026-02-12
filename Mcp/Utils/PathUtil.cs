using System;
using System.IO;
using System.Text.RegularExpressions;

namespace McpHost.Utils
{
    static class PathUtil
    {
        /// <summary>
        /// Normaliza rutas cuando se llama desde WSL hacia un .exe de Windows.
        /// Casos típicos:
        ///  - /mnt/d/Algo/...              -> D:\Algo\...
        ///  - D:\mnt\d\Algo\...        -> D:\Algo\...
        /// </summary>
        public static string NormalizePathArg(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;

            // Solo tiene sentido en Windows (en Linux esto puede romper rutas válidas).
            if (Path.DirectorySeparatorChar != '\\') return path;

            // Caso: /mnt/d/Desarrollo/...  -> D:\Desarrollo\...
            if (path.StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase) && path.Length >= 7)
            {
                char drive = char.ToUpperInvariant(path[5]);
                string rest = path.Substring(6).Replace('/', '\\');
                return drive + ":\\" + rest;
            }

            // Caso: D:\mnt\d\Desarrollo\... -> D:\Desarrollo\...
            // (esto pasa si WSL hace una conversión rara al invocar el exe).
            var m = Regex.Match(path, @"^([A-Za-z]):\\mnt\\([A-Za-z])\\(.*)$");
            if (m.Success)
            {
                char drive = char.ToUpperInvariant(m.Groups[2].Value[0]);
                string rest = m.Groups[3].Value;
                return drive + ":\\" + rest;
            }

            return path;
        }

        public static bool LooksLikeWslOnlyPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (Path.DirectorySeparatorChar != '\\') return false; // Solo para exe Windows.
            if (path.StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase)) return false;
            if (path.StartsWith("/", StringComparison.Ordinal)) return true;
            if (path.StartsWith("~", StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
