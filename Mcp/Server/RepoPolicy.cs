using System;
using System.IO;
using McpHost.Utils;

namespace McpHost.Server
{
    class RepoPolicy
    {
        readonly string _root;

        // Directories denied for all operations
        static readonly string[] DeniedDirs = { ".git", "node_modules", ".vs" };

        // Additional directories denied for write operations
        static readonly string[] WriteDeniedDirs = { "bin", "obj" };

        // Max file size for read (10 MB)
        const long MaxFileSize = 10 * 1024 * 1024;

        public RepoPolicy(string root)
        {
            if (string.IsNullOrEmpty(root))
                throw new ArgumentException("Root path is required");

            _root = Path.GetFullPath(root).TrimEnd('\\', '/');

            if (!Directory.Exists(_root))
                throw new ArgumentException("Root directory does not exist: " + _root);
        }

        public string Root { get { return _root; } }

        /// <summary>
        /// Resolves a relative or absolute path against the root, validates it's within bounds.
        /// </summary>
        /// <param name="forWrite">If true, also denies bin/ and obj/ directories.</param>
        public string ResolvePath(string path, bool forWrite = false)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Path is required");

            // Normalize WSL-style paths
            path = PathUtil.NormalizePathArg(path);

            string full;
            if (Path.IsPathRooted(path))
                full = Path.GetFullPath(path);
            else
                full = Path.GetFullPath(Path.Combine(_root, path));

            // Ensure within root
            if (!full.Equals(_root, StringComparison.OrdinalIgnoreCase) &&
                !full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Access denied: path is outside the repo root.\nPath: " + full + "\nRoot: " + _root);
            }

            // Check denied directories
            string relative = full.Substring(_root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (string denied in DeniedDirs)
            {
                if (StartsWithDir(relative, denied))
                    throw new InvalidOperationException(
                        "Access denied: path is in a restricted directory (" + denied + ").");
            }

            if (forWrite)
            {
                foreach (string denied in WriteDeniedDirs)
                {
                    if (ContainsDirComponent(relative, denied))
                        throw new InvalidOperationException(
                            "Write denied: path is in a build-output directory (" + denied + "). Only source files can be patched.");
                }
            }

            // Check file size if exists
            if (File.Exists(full))
            {
                var info = new FileInfo(full);
                if (info.Length > MaxFileSize)
                    throw new InvalidOperationException(
                        "File too large (" + info.Length + " bytes). Max: " + MaxFileSize + " bytes.");
            }

            return full;
        }

        static bool StartsWithDir(string relative, string dirName)
        {
            return relative.StartsWith(dirName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   relative.Equals(dirName, StringComparison.OrdinalIgnoreCase);
        }

        static bool ContainsDirComponent(string relative, string dirName)
        {
            // Check at any level: "foo\bin\bar" should match "bin"
            string sep = Path.DirectorySeparatorChar.ToString();
            if (relative.StartsWith(dirName + sep, StringComparison.OrdinalIgnoreCase)) return true;
            if (relative.Equals(dirName, StringComparison.OrdinalIgnoreCase)) return true;
            if (relative.IndexOf(sep + dirName + sep, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (relative.EndsWith(sep + dirName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
