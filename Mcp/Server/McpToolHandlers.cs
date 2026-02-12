using System;
using System.Collections.Generic;
using System.Text;
using System.Web.Script.Serialization;
using McpHost.Core;
using McpHost.Utils;

namespace McpHost.Server
{
    class ToolResult
    {
        public List<object> Content { get; set; }
        public bool IsError { get; set; }
    }

    class McpToolHandlers
    {
        readonly FileGateway _gateway;
        readonly RepoPolicy _policy;
        readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        // Max chars for file.read response content. Larger files should use file.read_range.
        const int MaxReadContentChars = 500_000;

        public McpToolHandlers(FileGateway gateway, RepoPolicy policy)
        {
            _gateway = gateway;
            _policy = policy;
        }

        public List<object> GetToolDefinitions()
        {
            var tools = new List<object>();

            // file.read
            tools.Add(new Dictionary<string, object>
            {
                { "name", "file.read" },
                { "description", "Read the full content of a file. Returns the text, a strict SHA-256 hash (over original bytes), and a whitespace-normalized hash. Always use these hashes when calling file.apply_patch_only." },
                { "inputSchema", new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "path", new Dictionary<string, object>
                                    {
                                        { "type", "string" },
                                        { "description", "Absolute or repo-relative path to the file." }
                                    }
                                }
                            }
                        },
                        { "required", new[] { "path" } }
                    }
                }
            });

            // file.read_range
            tools.Add(new Dictionary<string, object>
            {
                { "name", "file.read_range" },
                { "description", "Read a specific line range from a file (1-based, inclusive). Returns the requested lines with line numbers, plus the full-file hashes." },
                { "inputSchema", new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "path", new Dictionary<string, object>
                                    {
                                        { "type", "string" },
                                        { "description", "Absolute or repo-relative path to the file." }
                                    }
                                },
                                { "start_line", new Dictionary<string, object>
                                    {
                                        { "type", "integer" },
                                        { "description", "First line to read (1-based)." }
                                    }
                                },
                                { "end_line", new Dictionary<string, object>
                                    {
                                        { "type", "integer" },
                                        { "description", "Last line to read (1-based, inclusive)." }
                                    }
                                }
                            }
                        },
                        { "required", new[] { "path", "start_line", "end_line" } }
                    }
                }
            });

            // file.apply_patch_only
            tools.Add(new Dictionary<string, object>
            {
                { "name", "file.apply_patch_only" },
                { "description", "Apply a unified diff patch to a file. Requires the SHA-256 hash from the last file.read or file.read_range call to prevent editing stale content. The diff must be in standard unified diff format (git-style). Lines in the diff must start with ' ' (context), '+' (add), or '-' (remove)." },
                { "inputSchema", new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "path", new Dictionary<string, object>
                                    {
                                        { "type", "string" },
                                        { "description", "Absolute or repo-relative path to the file to patch." }
                                    }
                                },
                                { "hash", new Dictionary<string, object>
                                    {
                                        { "type", "string" },
                                        { "description", "SHA-256 hash (64 hex chars) from the last file.read. Use either the strict or whitespace-normalized hash." }
                                    }
                                },
                                { "diff", new Dictionary<string, object>
                                    {
                                        { "type", "string" },
                                        { "description", "Unified diff content (not a file path). Must include @@ hunk headers and context lines." }
                                    }
                                },
                                { "allow_large", new Dictionary<string, object>
                                    {
                                        { "type", "boolean" },
                                        { "description", "Set to true to allow patches touching more than 200 lines (up to 1000). Default: false." }
                                    }
                                }
                            }
                        },
                        { "required", new[] { "path", "hash", "diff" } }
                    }
                }
            });

            return tools;
        }

        public ToolResult CallTool(string name, Dictionary<string, object> arguments)
        {
            switch (name)
            {
                case "file.read": return HandleFileRead(arguments);
                case "file.read_range": return HandleFileReadRange(arguments);
                case "file.apply_patch_only": return HandleApplyPatch(arguments);
                default:
                    return new ToolResult
                    {
                        IsError = true,
                        Content = TextContent("Unknown tool: " + name)
                    };
            }
        }

        ToolResult HandleFileRead(Dictionary<string, object> args)
        {
            string path = GetStringArg(args, "path");
            if (string.IsNullOrEmpty(path))
                return ErrorResult("Missing required argument: path");

            string resolved = _policy.ResolvePath(path);
            var snap = _gateway.Read(resolved);

            if (snap.Text.Length > MaxReadContentChars)
                return ErrorResult(
                    "File too large for file.read (" + snap.Text.Length + " chars, max " + MaxReadContentChars + "). " +
                    "Use file.read_range to read specific sections.");

            // Unicode warning goes to stderr (not in content)
            if (UnicodeIssueUtil.ContainsInvalidUnicode(snap.Text))
            {
                Console.Error.WriteLine(
                    UnicodeIssueUtil.BuildInvalidUnicodeError(snap.Text,
                        "WARNING: File contains invalid Unicode characters (U+FFFD or U+FEFF).",
                        maxOccurrences: 3));
            }

            // Metadata as JSON block
            var meta = new Dictionary<string, object>
            {
                { "hash_strict", snap.Sha256 },
                { "hash_normalized", snap.Sha256NormalizedWhitespace },
                { "encoding", snap.Encoding.WebName },
                { "has_bom", snap.HasBom },
                { "newline", DescribeNewline(snap.NewLine) }
            };

            var content = new List<object>();
            content.Add(new Dictionary<string, object> { { "type", "text" }, { "text", snap.Text } });
            content.Add(new Dictionary<string, object> { { "type", "text" }, { "text", _json.Serialize(meta) } });

            return new ToolResult { Content = content, IsError = false };
        }

        ToolResult HandleFileReadRange(Dictionary<string, object> args)
        {
            string path = GetStringArg(args, "path");
            if (string.IsNullOrEmpty(path))
                return ErrorResult("Missing required argument: path");

            int startLine = GetIntArg(args, "start_line", 0);
            int endLine = GetIntArg(args, "end_line", 0);

            if (startLine <= 0) return ErrorResult("start_line must be >= 1");
            if (endLine <= 0) return ErrorResult("end_line must be >= 1");

            if (endLine < startLine)
            {
                int tmp = startLine;
                startLine = endLine;
                endLine = tmp;
            }

            string resolved = _policy.ResolvePath(path);
            var snap = _gateway.Read(resolved);

            if (UnicodeIssueUtil.ContainsInvalidUnicode(snap.Text))
            {
                Console.Error.WriteLine(
                    UnicodeIssueUtil.BuildInvalidUnicodeError(snap.Text,
                        "WARNING: File contains invalid Unicode characters (U+FFFD or U+FEFF).",
                        maxOccurrences: 3));
            }

            string baseText = WhitespaceNormalizeUtil.NormalizeNewlinesToLf(snap.Text);
            var lines = baseText.Split('\n');
            int maxLine = lines.Length;
            if (startLine > maxLine) startLine = maxLine;
            if (endLine > maxLine) endLine = maxLine;

            var sb = new StringBuilder();
            sb.AppendLine($"-----RANGE {startLine}-{endLine} (1-based)-----");
            for (int i = startLine; i <= endLine; i++)
            {
                string line = lines[i - 1] ?? "";
                sb.AppendLine($"{i,6} | {line}");
            }

            var meta = new Dictionary<string, object>
            {
                { "hash_strict", snap.Sha256 },
                { "hash_normalized", snap.Sha256NormalizedWhitespace },
                { "encoding", snap.Encoding.WebName },
                { "has_bom", snap.HasBom },
                { "newline", DescribeNewline(snap.NewLine) },
                { "total_lines", maxLine },
                { "range_start", startLine },
                { "range_end", endLine }
            };

            var content = new List<object>();
            content.Add(new Dictionary<string, object> { { "type", "text" }, { "text", sb.ToString() } });
            content.Add(new Dictionary<string, object> { { "type", "text" }, { "text", _json.Serialize(meta) } });

            return new ToolResult { Content = content, IsError = false };
        }

        ToolResult HandleApplyPatch(Dictionary<string, object> args)
        {
            string path = GetStringArg(args, "path");
            if (string.IsNullOrEmpty(path))
                return ErrorResult("Missing required argument: path");

            string hash = GetStringArg(args, "hash");
            if (string.IsNullOrEmpty(hash))
                return ErrorResult("Missing required argument: hash");

            string diff = GetStringArg(args, "diff");
            if (string.IsNullOrEmpty(diff))
                return ErrorResult("Missing required argument: diff");

            bool allowLarge = GetBoolArg(args, "allow_large", false);

            string resolved = _policy.ResolvePath(path, forWrite: true);
            var snap = _gateway.Read(resolved);
            _gateway.ApplyPatchOnly(snap, diff, hash, allowLarge);

            return new ToolResult
            {
                IsError = false,
                Content = TextContent("OK - patch applied successfully to " + path)
            };
        }

        // --- helpers ---

        static string GetStringArg(Dictionary<string, object> args, string key)
        {
            if (args == null || !args.ContainsKey(key)) return null;
            return args[key] as string;
        }

        static int GetIntArg(Dictionary<string, object> args, string key, int defaultValue)
        {
            if (args == null || !args.ContainsKey(key)) return defaultValue;
            var val = args[key];
            if (val is int i) return i;
            if (val is long l) return (int)l;
            if (val is string s && int.TryParse(s, out int parsed)) return parsed;
            return defaultValue;
        }

        static bool GetBoolArg(Dictionary<string, object> args, string key, bool defaultValue)
        {
            if (args == null || !args.ContainsKey(key)) return defaultValue;
            var val = args[key];
            if (val is bool b) return b;
            if (val is string s) return s.Equals("true", StringComparison.OrdinalIgnoreCase);
            return defaultValue;
        }

        static List<object> TextContent(string text)
        {
            return new List<object>
            {
                new Dictionary<string, object>
                {
                    { "type", "text" },
                    { "text", text }
                }
            };
        }

        ToolResult ErrorResult(string message)
        {
            return new ToolResult
            {
                IsError = true,
                Content = TextContent(message)
            };
        }

        static string DescribeNewline(string nl)
        {
            if (nl == "\r\n") return "CRLF";
            if (nl == "\n") return "LF";
            if (nl == "\r") return "CR";
            return "CRLF";
        }
    }
}
