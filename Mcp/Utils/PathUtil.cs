using System;
using System.IO;
using System.Text.RegularExpressions;

namespace McpHost.Utils
{
    static class PathUtil
    {
        /// <summary>
        /// Normaliza rutas estilo WSL o Git Bash hacia rutas de Windows.
        /// Casos típicos:
        ///  - /mnt/d/Algo/...              -> D:\Algo\...
        ///  - D:\mnt\d\Algo\...        -> D:\Algo\...
        ///  - /d/Algo/... (Git Bash)       -> D:\Algo\...
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

            // Caso: /d/Desarrollo/... (Git Bash / MSYS) -> D:\Desarrollo\...
            if (IsMsysDrivePath(path))
            {
                char drive = char.ToUpperInvariant(path[1]);
                string rest = path.Length > 3 ? path.Substring(3).Replace('/', '\\') : string.Empty;
                return drive + ":\\" + rest;
            }

            return path;
        }

        public static bool LooksLikeWslOnlyPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (Path.DirectorySeparatorChar != '\\') return false; // Solo para exe Windows.
            if (path.StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase)) return false;
            if (IsMsysDrivePath(path)) return false;
            if (path.StartsWith("/", StringComparison.Ordinal)) return true;
            if (path.StartsWith("~", StringComparison.Ordinal)) return true;
            return false;
        }

        // "/d" o "/d/..." (una sola letra de unidad, como las muestra Git Bash).
        static bool IsMsysDrivePath(string path)
        {
            if (path.Length < 2 || path[0] != '/') return false;
            char letter = path[1];
            bool isAsciiLetter = (letter >= 'a' && letter <= 'z') || (letter >= 'A' && letter <= 'Z');
            return isAsciiLetter && (path.Length == 2 || path[2] == '/');
        }
    }
}
