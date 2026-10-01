using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace McpHost.Utils
{
    enum PathKind { File, Directory, Any }

    // Arma el mensaje de "no existe" con pistas para corregir la ruta en un solo intento: la carpeta existente
    // más cercana, nombres parecidos ahí y, con un recorrido acotado, dónde hay algo con ese nombre cerca.
    static class PathSuggestionUtil
    {
        const int MaxSimilar = 8;
        const int MaxFound = 5;
        const int MaxDirectoriesVisited = 3000;
        const int SearchBudgetMs = 400;
        static readonly string[] SkippedDirectories = { ".git", ".svn", ".vs", "node_modules", "bin", "obj" };

        public static string BuildNotFoundMessage(string fullPath, string root, PathKind kind)
        {
            var sb = new StringBuilder();
            sb.Append(kind == PathKind.Directory ? "No existe la carpeta: " : kind == PathKind.File ? "No existe el archivo: " : "No existe la ruta: ");
            sb.Append(fullPath);

            try
            {
                string trimmed = fullPath.TrimEnd('\\', '/');
                string ancestor = FindExistingAncestor(trimmed, root);
                if (ancestor == null) return sb.ToString();

                sb.Append("\nCarpeta existente más cercana: ").Append(ancestor);

                var similar = SimilarEntries(ancestor, FirstMissingSegment(trimmed, ancestor));
                if (similar.Count > 0)
                    sb.Append("\nParecidos ahí: ").Append(string.Join(", ", similar));

                // Desde el root se baja menos: ahí cuelgan todos los repos.
                bool ancestorIsRoot = SamePath(ancestor, root);
                var found = FindByName(ancestor, Path.GetFileName(trimmed), kind, ancestorIsRoot ? 2 : 3);
                if (found.Count > 0)
                    sb.Append("\nCon ese nombre, cerca: ").Append(string.Join(", ", found));
            }
            catch
            {
                // Las pistas son una ayuda: nunca deben tapar el error original.
            }

            return sb.ToString();
        }

        static string FindExistingAncestor(string path, string root)
        {
            string current = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(current))
            {
                if (!IsWithin(current, root)) return Directory.Exists(root) ? root : null;
                if (Directory.Exists(current)) return current;
                current = Path.GetDirectoryName(current);
            }
            return null;
        }

        static bool IsWithin(string path, string root)
        {
            string p = path.TrimEnd('\\', '/');
            string r = root.TrimEnd('\\', '/');
            return p.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                   p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        static bool SamePath(string a, string b)
        {
            return string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        static string FirstMissingSegment(string path, string ancestor)
        {
            string rest = path.Substring(ancestor.TrimEnd('\\', '/').Length).TrimStart('\\', '/');
            int separator = rest.IndexOfAny(new[] { '\\', '/' });
            return separator >= 0 ? rest.Substring(0, separator) : rest;
        }

        static List<string> SimilarEntries(string directory, string name)
        {
            string wanted = Simplify(name);
            var scored = new List<KeyValuePair<int, string>>();
            if (wanted.Length == 0) return new List<string>();

            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                bool isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                if (isDirectory && IsSkipped(entry.Name)) continue;

                string candidate = Simplify(entry.Name);
                if (candidate.Length == 0) continue;

                int score;
                if (candidate == wanted)
                    score = 0;
                else if (Math.Min(candidate.Length, wanted.Length) >= 3 && (candidate.Contains(wanted) || wanted.Contains(candidate)))
                    score = 1;
                else
                {
                    int distance = Levenshtein(candidate, wanted, 3);
                    if (distance > Math.Max(1, wanted.Length / 4)) continue;
                    score = 1 + distance;
                }

                scored.Add(new KeyValuePair<int, string>(score, entry.Name + (isDirectory ? "\\" : "")));
            }

            return scored
                .OrderBy(s => s.Key)
                .ThenBy(s => s.Value, StringComparer.OrdinalIgnoreCase)
                .Take(MaxSimilar)
                .Select(s => s.Value)
                .ToList();
        }

        // Búsqueda en anchura acotada por profundidad, cantidad de carpetas y tiempo.
        static List<string> FindByName(string start, string name, PathKind kind, int maxDepth)
        {
            var found = new List<string>();
            if (string.IsNullOrEmpty(name)) return found;

            var watch = Stopwatch.StartNew();
            var queue = new Queue<KeyValuePair<string, int>>();
            queue.Enqueue(new KeyValuePair<string, int>(start, 0));
            int visited = 0;

            while (queue.Count > 0 && found.Count < MaxFound && visited < MaxDirectoriesVisited && watch.ElapsedMilliseconds < SearchBudgetMs)
            {
                var item = queue.Dequeue();
                visited++;

                FileSystemInfo[] entries;
                try { entries = new DirectoryInfo(item.Key).GetFileSystemInfos(); }
                catch { continue; }

                foreach (var entry in entries)
                {
                    bool isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;

                    if (entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                        (kind == PathKind.Any || (kind == PathKind.Directory) == isDirectory))
                    {
                        found.Add(entry.FullName);
                        if (found.Count >= MaxFound) break;
                    }

                    if (isDirectory && item.Value < maxDepth && !IsSkipped(entry.Name) &&
                        (entry.Attributes & FileAttributes.ReparsePoint) == 0)
                        queue.Enqueue(new KeyValuePair<string, int>(entry.FullName, item.Value + 1));
                }
            }

            return found;
        }

        static bool IsSkipped(string directoryName)
        {
            foreach (var skipped in SkippedDirectories)
                if (directoryName.Equals(skipped, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static string Simplify(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        // Distancia de edición con corte: si supera "max", devuelve max + 1.
        static int Levenshtein(string a, string b, int max)
        {
            if (Math.Abs(a.Length - b.Length) > max) return max + 1;

            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) previous[j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                int rowMin = current[0];
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                    if (current[j] < rowMin) rowMin = current[j];
                }
                if (rowMin > max) return max + 1;

                var swap = previous;
                previous = current;
                current = swap;
            }

            return Math.Min(previous[b.Length], max + 1);
        }
    }
}
