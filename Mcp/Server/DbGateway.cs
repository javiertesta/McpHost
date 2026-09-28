using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace McpHost.Server
{
    class DbQueryResult
    {
        public string Site { get; set; }
        public string ParametrizacionPath { get; set; }
        public DbConnectionInfo Connection { get; set; }
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
        // Palabras de escritura/DDL. INSERT, REPLACE y TRUNCATE también son funciones de MySQL (INSERT(str,pos,len,nuevo),
        // REPLACE(str,de,a), TRUNCATE(x,d)): seguidas de "(" son la función y se permiten. La sentencia igual no se puede
        // armar, porque la query tiene que empezar con SELECT/WITH y no puede tener ';'. OUTFILE/DUMPFILE: SELECT ...
        // INTO OUTFILE escribe un archivo en el servidor.
        static readonly Regex ForbiddenKeywords = new Regex(
            "\\b(?:UPDATE|DELETE|DROP|ALTER|CREATE|CALL|GRANT|REVOKE|MERGE|OUTFILE|DUMPFILE)\\b|\\b(?:INSERT|REPLACE|TRUNCATE)\\b(?!\\s*\\()",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // MySQL ejecuta lo que va en /*! ... */ (MariaDB, también /*M! ... */): no es un comentario.
        static readonly Regex ExecutableComment = new Regex("/\\*M?!", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex HtmlEntity = new Regex("&(?:lt|gt|amp|quot|apos|#[0-9]+|#x[0-9a-f]+);", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

            ResolvedDbConnection resolved = _resolver.Resolve(site);
            string cs = resolved.ConnectionString;

            using (DbConnection conn = CreateConnection(cs))
            {
                conn.Open();
                EnsureDatabase(conn, resolved);

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
                            Site = string.IsNullOrWhiteSpace(resolved.SiteArg) ? InferSite(site) : resolved.SiteArg,
                            ParametrizacionPath = resolved.ParametrizacionPath ?? "",
                            Connection = ToConnectionInfo(resolved),
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

            ResolvedDbConnection resolved = _resolver.Resolve(site);
            string cs = resolved.ConnectionString;

            using (DbConnection conn = CreateConnection(cs))
            {
                conn.Open();
                EnsureDatabase(conn, resolved);

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

            // Antes de sacar los comentarios del principio: un /*! ... */ ahí MySQL lo ejecuta.
            if (ExecutableComment.IsMatch(sql))
                throw new InvalidOperationException("No se permiten comentarios ejecutables (/*! ... */) en db.query/db.scalar.");

            string trimmed = StripLeadingComments(sql).Trim();
            if (trimmed.Length == 0)
                throw new InvalidOperationException("SQL vacío.");

            string normalized = trimmed;
            if (normalized.EndsWith(";", StringComparison.Ordinal))
                normalized = normalized.Substring(0, normalized.Length - 1).TrimEnd();

            // "(SELECT ... LIMIT 2) UNION ALL (SELECT ...)" también es una lectura.
            int start = 0;
            while (start < normalized.Length && (normalized[start] == '(' || char.IsWhiteSpace(normalized[start])))
                start++;
            string statement = normalized.Substring(start);
            if (!(statement.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ||
                  statement.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Solo se permiten queries SELECT (incluye CTE WITH). ");
            }

            // Un ';' o una palabra reservada dentro de un literal ('FALTA EL ALTER', SEPARATOR '; ') no es SQL. Los
            // literales se sacan sólo si dónde termina cada uno es lo mismo para MySQL con cualquier sql_mode: sin
            // barras invertidas (con NO_BACKSLASH_ESCAPES, \' cierra el literal) ni marcas de comentario (una comilla
            // dentro de un comentario no abre nada). Si no, se revisa el texto entero, como antes.
            bool literalsAreUnambiguous = sql.IndexOf('\\') < 0 && sql.IndexOf('#') < 0 &&
                sql.IndexOf("--", StringComparison.Ordinal) < 0 && sql.IndexOf("/*", StringComparison.Ordinal) < 0;
            string code = literalsAreUnambiguous ? RemoveQuotedText(normalized) : normalized;

            if (code.Contains(";"))
            {
                if (HtmlEntity.IsMatch(code))
                    throw new InvalidOperationException("El SQL trae entidades HTML (&lt; &gt; &amp;) en vez de los caracteres: mandá <, > y & tal cual.");
                throw new InvalidOperationException("No se permiten múltiples sentencias ni ';' en db.query/db.scalar.");
            }

            Match forbidden = ForbiddenKeywords.Match(code);
            if (forbidden.Success)
                throw new InvalidOperationException("Query rechazada: contiene palabras reservadas de escritura/DDL (" + forbidden.Value.ToUpperInvariant() + ").");
        }

        static string StripLeadingComments(string sql)
        {
            string s = sql ?? "";
            int i = 0;
            while (i < s.Length)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                // Como MySQL: "--" abre comentario sólo si le sigue un espacio o un carácter de control, y el comentario
                // llega hasta el '\n'. Si se cortara antes (p. ej. en un '\r'), se validaría como query lo que MySQL
                // saltea, y MySQL ejecutaría sin validar lo que viene en la línea siguiente.
                if (i + 1 < s.Length && s[i] == '-' && s[i + 1] == '-' &&
                    (i + 2 == s.Length || s[i + 2] <= ' ' || s[i + 2] == '\u007f'))
                {
                    i += 2;
                    while (i < s.Length && s[i] != '\n') i++;
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

        // El texto con cada literal ('...', "...") o identificador entre backticks reemplazado por un espacio. Una
        // comilla duplicada ('') es una comilla escapada. Si alguno queda sin cerrar, devuelve el texto tal cual.
        static string RemoveQuotedText(string text)
        {
            var sb = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c != '\'' && c != '"' && c != '`')
                {
                    sb.Append(c);
                    i++;
                    continue;
                }

                int j = i + 1;
                while (true)
                {
                    if (j >= text.Length) return text;
                    if (text[j] != c) j++;
                    else if (j + 1 < text.Length && text[j + 1] == c) j += 2;
                    else break;
                }

                sb.Append(' ');
                i = j + 1;
            }
            return sb.ToString();
        }

        DbConnection CreateConnection(string connectionString)
        {
            var factory = GetMySqlFactory();
            DbConnection conn = factory.CreateConnection();
            if (conn == null) throw new InvalidOperationException("No se pudo crear conexión MySQL.");
            conn.ConnectionString = connectionString;
            return conn;
        }

        static void EnsureDatabase(DbConnection conn, ResolvedDbConnection resolved)
        {
            if (conn == null) return;
            if (resolved == null) return;

            // If the connection string specifies a database, enforce it explicitly.
            // This avoids cases where the provider ends up in the account's default schema.
            string db = (resolved.Database ?? "").Trim();
            if (db.Length == 0) return;

            try
            {
                conn.ChangeDatabase(db);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "No se pudo seleccionar la base '" + db + "' tras abrir la conexión. Source=" + resolved.SourceDescription,
                    ex);
            }
        }

        static DbConnectionInfo ToConnectionInfo(ResolvedDbConnection resolved)
        {
            if (resolved == null) return null;
            return new DbConnectionInfo
            {
                Source = resolved.SourceDescription,
                Server = resolved.Server,
                Port = resolved.Port,
                Database = resolved.Database,
                UserId = resolved.UserId,
                ParametrizacionPath = resolved.ParametrizacionPath ?? ""
            };
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
                    @"D:\\Desarrollo\\DLLS\\datos\\MySql.Data.dll",
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

                throw new InvalidOperationException("No se pudo cargar MySql.Data.dll. Definí MCP_MYSQL_DATA_DLL_PATH o asegurá D:\\\\Desarrollo\\\\DLLS\\\\datos\\\\MySql.Data.dll.", last);
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
