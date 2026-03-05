using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace McpHost.Core
{
    class OutlineItem
    {
        public int Line;
        public string Kind;
        public string Name;
        public string Access;
    }

    static class OutlineEngine
    {
        static readonly Regex VbPattern = new Regex(
            @"^\s*(?<access>Public|Private|Protected|Friend|Protected Friend)?\s*" +
            @"(?<modifier>Shared|Overrides|Overridable|MustOverride|NotOverridable|Shadows|ReadOnly|WriteOnly|Async|Iterator)?\s*" +
            @"(?<kind>Sub|Function|Property|Class|Module|Interface|Enum|Structure)\s+(?<name>\w+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static readonly Regex CsTypePattern = new Regex(
            @"^\s*(?<access>public|private|protected|internal)?\s*(static|abstract|sealed|partial)?\s*(?<kind>class|interface|enum|struct|record)\s+(?<name>\w+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static readonly Regex CsMemberPattern = new Regex(
            @"^\s*(?<access>public|private|protected|internal)?\s*(static|virtual|override|abstract|async|sealed)?\s*\S+\s+(?<name>\w+)\s*[({]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static List<OutlineItem> GetOutline(string text, string fileExtension)
        {
            var items = new List<OutlineItem>();
            string ext = (fileExtension ?? "").ToLowerInvariant();
            string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

            if (ext == ".vb")
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    var m = VbPattern.Match(lines[i]);
                    if (!m.Success) continue;
                    items.Add(new OutlineItem
                    {
                        Line = i + 1,
                        Kind = m.Groups["kind"].Value,
                        Name = m.Groups["name"].Value,
                        Access = m.Groups["access"].Value
                    });
                }
            }
            else if (ext == ".cs")
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];

                    var mt = CsTypePattern.Match(line);
                    if (mt.Success)
                    {
                        items.Add(new OutlineItem
                        {
                            Line = i + 1,
                            Kind = mt.Groups["kind"].Value,
                            Name = mt.Groups["name"].Value,
                            Access = mt.Groups["access"].Value
                        });
                        continue;
                    }

                    // Skip variable declarations (lines ending with ;)
                    if (line.TrimEnd().EndsWith(";")) continue;

                    var mm = CsMemberPattern.Match(line);
                    if (mm.Success)
                    {
                        items.Add(new OutlineItem
                        {
                            Line = i + 1,
                            Kind = "member",
                            Name = mm.Groups["name"].Value,
                            Access = mm.Groups["access"].Value
                        });
                    }
                }
            }
            else
            {
                throw new InvalidOperationException(
                    $"Extensión '{ext}' no soportada por file.outline. Extensiones soportadas: .vb, .cs");
            }

            return items;
        }
    }
}
