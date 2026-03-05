using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace McpHost.Core
{
    class GrepResult
    {
        public string File;
        public int Line;
        public string Text;
        public bool IsContext;
    }

    static class GrepEngine
    {
        public static List<GrepResult> Search(
            string pattern,
            string searchPath,
            string glob,
            int contextLines,
            bool caseInsensitive,
            int maxMatches,
            bool multiline)
        {
            string rgExe = McpConfig.Instance.RgExePath;
            if (rgExe == null)
                throw new InvalidOperationException(
                    "rg.exe no encontrado junto a Mcp.exe.\n" +
                    "Copiá rg.exe (ripgrep) junto a Mcp.exe para habilitar file.grep.");

            var argsList = new List<string> { "--json" };
            if (caseInsensitive) argsList.Add("-i");
            if (contextLines > 0) { argsList.Add("-C"); argsList.Add(contextLines.ToString()); }
            if (!string.IsNullOrEmpty(glob)) { argsList.Add("-g"); argsList.Add(QuoteArg(glob)); }
            if (maxMatches > 0) { argsList.Add("--max-count"); argsList.Add(maxMatches.ToString()); }
            if (multiline) argsList.Add("-U");
            argsList.Add(QuoteArg(pattern));
            argsList.Add(QuoteArg(searchPath));

            string args = string.Join(" ", argsList);

            var psi = new ProcessStartInfo
            {
                FileName = rgExe,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false)
            };

            var results = new List<GrepResult>();
            int matchCount = 0;
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

            using (var proc = Process.Start(psi))
            {
                proc.ErrorDataReceived += (s, e) => { };
                proc.BeginErrorReadLine();
                bool hitLimit = false;
                string line;
                while ((line = proc.StandardOutput.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    Dictionary<string, object> obj;
                    try { obj = serializer.Deserialize<Dictionary<string, object>>(line); }
                    catch { continue; }

                    object typeObj;
                    if (!obj.TryGetValue("type", out typeObj)) continue;
                    string type = typeObj as string;
                    if (type != "match" && type != "context") continue;

                    object dataObj;
                    if (!obj.TryGetValue("data", out dataObj)) continue;
                    var data = dataObj as Dictionary<string, object>;
                    if (data == null) continue;

                    string filePath = GetNestedString(data, "path", "text");
                    int lineNumber = GetNestedInt(data, "line_number");
                    string lineText = GetNestedString(data, "lines", "text");

                    if (lineText != null)
                        lineText = lineText.TrimEnd('\r', '\n');

                    bool isContext = type == "context";
                    if (!isContext)
                    {
                        matchCount++;
                        if (maxMatches > 0 && matchCount > maxMatches) { hitLimit = true; break; }
                    }

                    results.Add(new GrepResult
                    {
                        File = filePath,
                        Line = lineNumber,
                        Text = lineText ?? "",
                        IsContext = isContext
                    });
                }
                if (hitLimit) ForceKill(proc);
                else if (!proc.WaitForExit(60000)) ForceKill(proc);
            }

            return results;
        }

        static string GetNestedString(Dictionary<string, object> data, string key1, string key2)
        {
            object val1;
            if (!data.TryGetValue(key1, out val1)) return null;
            var nested = val1 as Dictionary<string, object>;
            if (nested == null) return null;
            object val2;
            if (!nested.TryGetValue(key2, out val2)) return null;
            return val2 as string;
        }

        static int GetNestedInt(Dictionary<string, object> data, string key)
        {
            object val;
            if (!data.TryGetValue(key, out val)) return 0;
            if (val is int i) return i;
            if (val is long l) return (int)l;
            return 0;
        }

        static string QuoteArg(string arg)
            => "\"" + arg.Replace("\"", "\\\"") + "\"";

        static void ForceKill(Process p)
            { try { p.Kill(); } catch { } }
    }
}
