using System;
using System.Collections.Generic;
using McpHost.Utils;

namespace McpHost.Diff
{
    static class UnifiedDiffApplier
    {
        public static string Apply(string originalText, UnifiedDiff diff)
        {
            var originalLines = new List<string>(originalText.Split('\n'));

            var result = new List<string>(originalLines);
            int offset = 0;

            foreach (var hunk in diff.Hunks)
            {
                int index = hunk.StartOriginal - 1 + offset;

                foreach (string line in hunk.Lines)
                {
                    if (line.StartsWith("-"))
                    {
                        result.RemoveAt(index);
                        offset--;
                    }
                    else if (line.StartsWith("+"))
                    {
                        result.Insert(index, line.Substring(1));
                        index++;
                        offset++;
                    }
                    else if (line.StartsWith(" "))
                    {
                        index++;
                    }
                }
            }

            // Bloquear caracteres peligrosos
            string resultText = string.Join("\n", result);
            if (UnicodeIssueUtil.ContainsInvalidUnicode(resultText))
            {
                throw new InvalidOperationException(
                    UnicodeIssueUtil.BuildInvalidUnicodeError(
                        resultText,
                        "Carácter Unicode inválido detectado en el resultado del patch (U+FFFD o U+FEFF)."
                    )
                );
            }
            return resultText;
        }
    }
}
