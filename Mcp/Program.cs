using McpHost.Core;
using McpHost.Server;
using McpHost.Utils;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace McpHost
{
    class Program
    {
        static int Main(string[] args)
        {
            try
            {
                var gateway = new FileGateway();

                Console.OutputEncoding = new UTF8Encoding(false);
                Console.InputEncoding = Encoding.UTF8;
                if (args.Length == 0)
                {
                    PrintHelp();
                    return 1;
                }

                if (IsHelpArg(args[0]))
                {
                    PrintHelp();
                    return 0;
                }

                switch (args[0].ToLower())
                {
                    case "read":
                        return CmdRead(gateway, args);

                    case "apply-patch":
                        return CmdApplyPatch(gateway, args);

                    case "read-range":
                        return CmdReadRange(gateway, args);

                    case "serve":
                        return CmdServeMcp(gateway, args);

                    default:
                        throw new ArgumentException("Comando desconocido: " + args[0] + ". Usá 'help' para ver comandos.");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERROR: " + ex.Message);
                return 1;
            }
        }

        static bool IsHelpArg(string arg)
        {
            if (string.IsNullOrWhiteSpace(arg)) return false;
            string a = arg.Trim();
            return a.Equals("help", StringComparison.OrdinalIgnoreCase) ||
                   a.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                   a.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
                   a.Equals("/?", StringComparison.OrdinalIgnoreCase) ||
                   a.Equals("?", StringComparison.OrdinalIgnoreCase);
        }

        static void PrintHelp()
        {
            Console.WriteLine("MCP Host (Mcp.exe)");
            Console.WriteLine("Comandos:");
            Console.WriteLine("  read <path>");
            Console.WriteLine("  read-range <path> <startLine> <endLine>");
            Console.WriteLine("  apply-patch <path> <hash> <diffFile> [--large]");
            Console.WriteLine("  serve --root <repoRoot>");
            Console.WriteLine();
            Console.WriteLine("Notas:");
            Console.WriteLine("  - En apply-patch, diffFile es una RUTA a un archivo .diff (unified diff), no el contenido del diff inline.");
            Console.WriteLine("  - Si invocás desde WSL, usá /mnt/<drive>/... (ej. /mnt/d/...) para que exista en Windows.");
            Console.WriteLine("  - El hash debe ser el valor completo (64 hex) que imprime el comando read.");
            Console.WriteLine("  - serve: inicia el MCP server por stdio (JSON-RPC 2.0). Requiere --root para definir el directorio raíz del repo.");
        }

        static bool LooksLikeUnifiedDiffInline(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            if (value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0) return true;

            string v = value.TrimStart();
            if (v.StartsWith("@@")) return true;
            if (v.StartsWith("---") || v.StartsWith("+++")) return true;
            if (v.StartsWith("diff --git", StringComparison.OrdinalIgnoreCase)) return true;

            if (v.Contains("@@ -")) return true;
            return false;
        }

        static int CmdRead(FileGateway gateway, string[] args)
        {
            if (args.Length < 2) throw new ArgumentException("read <path>");

            string path = PathUtil.NormalizePathArg(args[1]);

            var snap = gateway.Read(path);

            // Advertencia fuera del STDOUT (para no contaminar el contenido del archivo)
            if (UnicodeIssueUtil.ContainsInvalidUnicode(snap.Text))
            {
                Console.Error.WriteLine(
                    UnicodeIssueUtil.BuildInvalidUnicodeError(
                        snap.Text,
                        "WARNING: El archivo contiene caracteres Unicode inválidos (U+FFFD o U+FEFF).",
                        maxOccurrences: 3
                    )
                );
                Console.Error.WriteLine();
            }

            Console.WriteLine(snap.Text);
            Console.WriteLine("-----HASH-----");
            Console.WriteLine(snap.Sha256NormalizedWhitespace);
            Console.WriteLine("-----HASH-STRICT-----");
            Console.WriteLine(snap.Sha256);

            return 0;
        }

        static int CmdApplyPatch(FileGateway gateway, string[] args)
        {
            bool allowLarge = args.Contains("--large");
            if (args.Length < 4) throw new ArgumentException("apply-patch <path> <hash> <diffFile>. Usá 'help' para ejemplos.");

            string rawDiffArg = args[3];
            if (LooksLikeUnifiedDiffInline(rawDiffArg))
                throw new ArgumentException("apply-patch espera diffFile como ruta a un archivo .diff; recibí contenido de diff inline. Guardalo en un archivo (ej. /mnt/d/.../patch.diff) y pasá esa ruta. Usá 'help' para ejemplos.");

            if (PathUtil.LooksLikeWslOnlyPath(rawDiffArg))
                throw new ArgumentException("diffFile apunta a una ruta Linux/WSL (por ejemplo /tmp, /home, etc.). El .exe corre en Windows: creá el archivo en /mnt/<drive>/... y pasá esa ruta.");

            string path = PathUtil.NormalizePathArg(args[1]);
            string hash = args[2];
            string diffPath = PathUtil.NormalizePathArg(rawDiffArg);

            string diffText;
            try
            {
                diffText = File.ReadAllText(diffPath);
            }
            catch (ArgumentException)
            {
                throw new ArgumentException("diffFile inválido. Debe ser una ruta a un archivo .diff. No pegues el diff inline; guardalo en un archivo y pasá esa ruta. Usá 'help' para ejemplos.");
            }
            catch (FileNotFoundException)
            {
                throw new ArgumentException("No existe diffFile: " + diffPath);
            }
            catch (DirectoryNotFoundException)
            {
                throw new ArgumentException("No existe el directorio de diffFile: " + diffPath);
            }
            var snap = gateway.Read(path);
            gateway.ApplyPatchOnly(snap, diffText, hash, allowLarge);

            Console.WriteLine("OK");
            return 0;
        }

        static int CmdReadRange(FileGateway gateway, string[] args)
        {
            if (args.Length < 4) throw new ArgumentException("read-range <path> <startLine> <endLine>");

            string path = PathUtil.NormalizePathArg(args[1]);
            if (!int.TryParse(args[2], out int startLine) || startLine <= 0)
                throw new ArgumentException("startLine inválida (debe ser >= 1)");
            if (!int.TryParse(args[3], out int endLine) || endLine <= 0)
                throw new ArgumentException("endLine inválida (debe ser >= 1)");
            if (endLine < startLine)
            {
                int tmp = startLine;
                startLine = endLine;
                endLine = tmp;
            }

            var snap = gateway.Read(path);

            if (UnicodeIssueUtil.ContainsInvalidUnicode(snap.Text))
            {
                Console.Error.WriteLine(
                    UnicodeIssueUtil.BuildInvalidUnicodeError(
                        snap.Text,
                        "WARNING: El archivo contiene caracteres Unicode inválidos (U+FFFD o U+FEFF).",
                        maxOccurrences: 3
                    )
                );
                Console.Error.WriteLine();
            }

            // Split consistente (LF) para que los rangos sean estables.
            string baseText = WhitespaceNormalizeUtil.NormalizeNewlinesToLf(snap.Text);
            var lines = baseText.Split('\n');

            int maxLine = lines.Length;
            if (startLine > maxLine) startLine = maxLine;
            if (endLine > maxLine) endLine = maxLine;

            Console.WriteLine($"-----RANGE {startLine}-{endLine} (1-based)-----");
            for (int i = startLine; i <= endLine; i++)
            {
                string line = lines[i - 1] ?? "";
                Console.WriteLine($"{i,6} | {line}");
            }

            Console.WriteLine("-----HASH-----");
            Console.WriteLine(snap.Sha256NormalizedWhitespace);
            Console.WriteLine("-----HASH-STRICT-----");
            Console.WriteLine(snap.Sha256);

            return 0;
        }

        static int CmdServeMcp(FileGateway gateway, string[] args)
        {
            string root = ".";
            for (int i = 1; i < args.Length - 1; i++)
            {
                if (args[i].Equals("--root", StringComparison.OrdinalIgnoreCase))
                {
                    root = args[i + 1];
                    i++;
                }
            }

            root = PathUtil.NormalizePathArg(root);
            var policy = new RepoPolicy(Path.GetFullPath(root));
            var handlers = new McpToolHandlers(gateway, policy);
            var server = new StdioMcpServer(handlers);
            server.Run();
            return 0;
        }
    }
}
