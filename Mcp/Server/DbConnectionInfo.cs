namespace McpHost.Server
{
    class DbConnectionInfo
    {
        public string Source { get; set; }              // env:VAR or xml:PATH
        public string Server { get; set; }
        public int? Port { get; set; }
        public string Database { get; set; }
        public string UserId { get; set; }
        public string ParametrizacionPath { get; set; } // empty if env source
    }
}
