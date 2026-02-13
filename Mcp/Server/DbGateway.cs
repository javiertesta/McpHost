using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace McpHost.Server
{
    class DbQueryResult
    {
        public string Site { get; set; }
        public string ParametrizacionPath { get; set; }
        public List<string> Columns { get; set; }
        public List<List<object>> Rows { get; set; }
        public int RowCount { get; set; }
        public bool Truncated { get; set; }
    }

    class DbGateway
    {
        readonly DbConnectionResolver _resolver;
        readonly string _root;

        static readonly Regex SiteDirRegex = new Regex("^[A-Za-z]{3}[0-9]{3}P$", RegexOptions.Compiled);
        static readonly Regex ForbiddenKeywords = new Regex("\\b(INSERT|UPDATE|DELETE|DROP|ALTER|CREATE|TRUNCATE|REPLACE|CALL|GRANT|REVOKE|MERGE)\\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static readonly object FactoryLock = new object();
        static DbProviderFactory _factory;

        public DbGateway(string root)
        {
            _root = root;
            _resolver = new DbConnectionResolver(root);
        }

        public DbQueryResult ExecuteQuery(string sql, string site, int maxRows)
        {
            ValidateReadOnlySql(sql);
            if (maxRows <= 0) maxRows = 200;
            if (maxRows > 2000) maxRows = 2000;

            string cs = _resolver.ResolveConnectionString(site);
            using (DbConnection conn = CreateConnection(cs))
            {
                conn.Open();
                using (DbCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    cmd.CommandTimeout = 900;

                    using (DbDataReader reader = cmd.ExecuteReader())
                    {
                        var columns = new List<string>();
                        for (int i = 0; i < reader.FieldCount; i++)
                            columns.Add(reader.GetName(i));

                        var rows = new List<List<object>>();
                        bool truncated = false;
                        while (reader.Read())
                        {
                            if (rows.Count >= maxRows)
                            {
                                truncated = true;
                                break;
                            }

                            var row = new List<object>();
                            for (int i = 0; i < reader.FieldCount; i++)
                                row.Add(NormalizeDbValue(reader.GetValue(i)));

                            rows.Add(row);
                        }

                        return new DbQueryResult
                        {
                            Site = InferSite(site),
                            ParametrizacionPath = InferParametrizacionPath(site),
                            Columns = columns,
                            Rows = rows,
                            RowCount = rows.Count,
                            Truncated = truncated
                        };
                    }
                }
            }
        }

        public object ExecuteScalar(string sql, string site)
        {
            ValidateReadOnlySql(sql);
            string cs = _resolver.ResolveConnectionString(site);
            using (DbConnection conn = CreateConnection(cs))
            {
                conn.Open();
                using (DbCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    cmd.CommandTimeout = 900;
                    return NormalizeDbValue(cmd.ExecuteScalar());
                }
            }
        }

        static object NormalizeDbValue(object value)
        {
            if (value == null || value == DBNull.Value) return null;
            if (value is byte[]) return Convert.ToBase64String((byte[])value);
            if (value is DateTime) return ((DateTime)value).ToString("o");
            if (value is TimeSpan) return value.ToString();
            return value;
        }

        static void ValidateReadOnlySql(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                throw new InvalidOperationException("SQL vacío.");

            string trimmed = StripLeadingComments(sql).Trim();
            if (trimmed.Length == 0)
                throw new InvalidOperationException("SQL vacío.");

            string normalized = trimmed;
            if (normalized.EndsWith(";", StringComparison.Ordinal))
                normalized = normalized.Substring(0, normalized.Length - 1).TrimEnd();

            if (!(normalized.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ||
                  normalized.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Solo se permiten queries SELECT (incluye CTE WITH). ");
            }

            if (normalized.Contains(";"))
                throw new InvalidOperationException("No se permiten múltiples sentencias ni ';' en db.query/db.scalar.");

            if (ForbiddenKeywords.IsMatch(normalized))
                throw new InvalidOperationException("Query rechazada: contiene palabras reservadas de escritura/DDL.");
        }

        static string StripLeadingComments(string sql)
        {
            string s = sql ?? "";
            int i = 0;
            while (i < s.Length)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                if (i + 1 < s.Length && s[i] == '-' && s[i + 1] == '-')
                {
                    i += 2;
                    while (i < s.Length && s[i] != '\n' && s[i] != '\r') i++;
                    continue;
                }

                if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++;
                    if (i + 1 < s.Length) i += 2;
                    continue;
                }

                break;
            }

            return s.Substring(i);
        }

        DbConnection CreateConnection(string connectionString)
        {
            var factory = GetMySqlFactory();
            DbConnection conn = factory.CreateConnection();
            if (conn == null) throw new InvalidOperationException("No se pudo crear conexión MySQL.");
            conn.ConnectionString = connectionString;
            return conn;
        }

        static DbProviderFactory GetMySqlFactory()
        {
            if (_factory != null) return _factory;

            lock (FactoryLock)
            {
                if (_factory != null) return _factory;

                string[] candidates =
                {
                    Environment.GetEnvironmentVariable("MCP_MYSQL_DATA_DLL_PATH"),
                    @"D:\Desarrollo\DLLS\datos\MySql.Data.dll",
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MySql.Data.dll")
                };

                Exception last = null;
                foreach (string candidate in candidates)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(candidate)) continue;
                        if (!File.Exists(candidate)) continue;

                        Assembly asm = Assembly.LoadFrom(candidate);
                        Type t = asm.GetType("MySql.Data.MySqlClient.MySqlClientFactory", throwOnError: true);
                        FieldInfo f = t.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                        _factory = (DbProviderFactory)f.GetValue(null);
                        return _factory;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                    }
                }

                throw new InvalidOperationException("No se pudo cargar MySql.Data.dll. Definí MCP_MYSQL_DATA_DLL_PATH o asegurá D:\\Desarrollo\\DLLS\\datos\\MySql.Data.dll.", last);
            }
        }

        string InferSite(string site)
        {
            if (!string.IsNullOrWhiteSpace(site)) return site.Trim();

            foreach (string path in Directory.EnumerateFiles(_root, "parametrizacion.xml", SearchOption.AllDirectories))
            {
                string parent = Path.GetFileName(Path.GetDirectoryName(path));
                if (SiteDirRegex.IsMatch(parent)) return parent;
            }

            return "";
        }

        string InferParametrizacionPath(string site)
        {
            if (!string.IsNullOrWhiteSpace(site))
            {
                string requested = site.Trim();
                if (Path.IsPathRooted(requested))
                    return requested.EndsWith("parametrizacion.xml", StringComparison.OrdinalIgnoreCase)
                        ? requested
                        : Path.Combine(requested, "parametrizacion.xml");

                string rel = Path.Combine(_root, requested, "parametrizacion.xml");
                if (File.Exists(rel)) return rel;
            }

            foreach (string path in Directory.EnumerateFiles(_root, "parametrizacion.xml", SearchOption.AllDirectories))
            {
                string parent = Path.GetFileName(Path.GetDirectoryName(path));
                if (SiteDirRegex.IsMatch(parent)) return path;
            }

            return "";
        }
    }
}
