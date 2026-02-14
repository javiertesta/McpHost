using System;

namespace McpHost.Server
{
    class ResolvedDbConnection
    {
        // Echo of the caller-provided site argument (trimmed). Empty means auto mode.
        public string SiteArg { get; set; }

        // "xml" or "env"
        public string SourceKind { get; set; }

        // For env source, the environment variable name used.
        public string EnvVarName { get; set; }

        // For xml source, the actual parametrizacion.xml path used.
        public string ParametrizacionPath { get; set; }

        // The normalized connection string used to open the connection (may include password).
        public string ConnectionString { get; set; }

        // Parsed (best-effort) safe summary fields.
        public string Server { get; set; }
        public int? Port { get; set; }
        public string Database { get; set; }
        public string UserId { get; set; }

        public string SourceDescription
        {
            get
            {
                if (string.Equals(SourceKind, "env", StringComparison.OrdinalIgnoreCase))
                    return "env:" + (EnvVarName ?? "");
                if (string.Equals(SourceKind, "xml", StringComparison.OrdinalIgnoreCase))
                    return "xml:" + (ParametrizacionPath ?? "");
                return SourceKind ?? "";
            }
        }
    }
}
