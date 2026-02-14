using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;

namespace McpHost.Server
{
    class DbConnectionResolver
    {
        readonly string _root;

        static readonly Regex SiteDirRegex = new Regex("^[A-Za-z]{3}[0-9]{3}P$", RegexOptions.Compiled);

        public DbConnectionResolver(string root)
        {
            _root = root;
        }

        public ResolvedDbConnection Resolve(string site)
        {
            string siteArg = (site ?? "").Trim();

            string envVarName;
            string envValue = ResolveFromEnvironment(siteArg, out envVarName);
            if (!string.IsNullOrWhiteSpace(envValue))
            {
                string normalized = NormalizeConnectionString(envValue);
                return BuildResolved(
                    sourceKind: "env",
                    envVarName: envVarName,
                    parametrizacionPath: "",
                    connectionString: normalized,
                    siteArg: siteArg);
            }

            string parametrizacionPath = ResolveParametrizacionPath(siteArg);
            string encrypted = ReadBaseDeDatos(parametrizacionPath);
            string decrypted = DbCrypto.Decrypt(encrypted);
            string cs = NormalizeConnectionString(decrypted);

            return BuildResolved(
                sourceKind: "xml",
                envVarName: "",
                parametrizacionPath: parametrizacionPath,
                connectionString: cs,
                siteArg: siteArg);
        }

        static string ResolveFromEnvironment(string siteArg, out string envVarName)
        {
            envVarName = null;

            // If the caller provided a site, only accept a site-specific override.
            // This avoids surprising behavior where a generic env var silently overrides the repo XML.
            if (!string.IsNullOrWhiteSpace(siteArg))
            {
                string normalized = Regex.Replace(siteArg.ToUpperInvariant(), "[^A-Z0-9]", "_");
                string name = "MCP_DB_CONNECTION_STRING_" + normalized;
                string siteVar = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrWhiteSpace(siteVar))
                {
                    envVarName = name;
                    return siteVar;
                }

                return null;
            }

            const string generic = "MCP_DB_CONNECTION_STRING";
            string genericVar = Environment.GetEnvironmentVariable(generic);
            if (!string.IsNullOrWhiteSpace(genericVar))
            {
                envVarName = generic;
                return genericVar;
            }

            return null;
        }

        string ResolveParametrizacionPath(string siteArg)
        {
            if (!string.IsNullOrWhiteSpace(siteArg))
            {
                string requested = siteArg.Trim();

                if (Path.IsPathRooted(requested))
                {
                    if (Directory.Exists(requested))
                    {
                        string full = Path.Combine(requested, "parametrizacion.xml");
                        if (File.Exists(full)) return full;
                    }
                    if (File.Exists(requested) && Path.GetFileName(requested).Equals("parametrizacion.xml", StringComparison.OrdinalIgnoreCase))
                        return requested;

                    throw new InvalidOperationException("No se encontró parametrizacion.xml en la ruta indicada: " + requested);
                }

                string asRelativeDir = Path.Combine(_root, requested);
                if (Directory.Exists(asRelativeDir))
                {
                    string full = Path.Combine(asRelativeDir, "parametrizacion.xml");
                    if (File.Exists(full)) return full;
                }

                var matchesByName = new List<string>();
                foreach (string path in Directory.EnumerateFiles(_root, "parametrizacion.xml", SearchOption.AllDirectories))
                {
                    string parent = Path.GetFileName(Path.GetDirectoryName(path));
                    if (parent.Equals(requested, StringComparison.OrdinalIgnoreCase))
                        matchesByName.Add(path);
                }

                if (matchesByName.Count == 1) return matchesByName[0];
                if (matchesByName.Count > 1)
                    throw new InvalidOperationException("Hay múltiples parametrizacion.xml para site='" + requested + "'. Pasá la ruta exacta.");

                throw new InvalidOperationException("No se encontró parametrizacion.xml para site='" + requested + "' dentro del root del repo.");
            }

            var candidates = new List<string>();
            foreach (string path in Directory.EnumerateFiles(_root, "parametrizacion.xml", SearchOption.AllDirectories))
            {
                string parent = Path.GetFileName(Path.GetDirectoryName(path));
                if (SiteDirRegex.IsMatch(parent))
                    candidates.Add(path);
            }

            if (candidates.Count == 1) return candidates[0];
            if (candidates.Count == 0)
                throw new InvalidOperationException("No se encontró una estructura XXX000P\\parametrizacion.xml dentro del repo actual.");

            throw new InvalidOperationException("Se encontraron múltiples parametrizacion.xml en carpetas XXX000P. Usá el argumento 'site' para elegir uno.");
        }

        static string ReadBaseDeDatos(string parametrizacionPath)
        {
            var xml = new XmlDocument();
            xml.Load(parametrizacionPath);

            XmlNode node = xml.SelectSingleNode("//baseDeDatos");
            if (node == null)
                throw new InvalidOperationException("El archivo parametrizacion.xml no contiene el tag <baseDeDatos>.");

            string value = (node.InnerText ?? "").Trim();
            if (value.Length == 0)
                throw new InvalidOperationException("El tag <baseDeDatos> está vacío en: " + parametrizacionPath);

            return value;
        }

        static string NormalizeConnectionString(string value)
        {
            string cs = (value ?? "").Trim();
            if (cs.Length == 0)
                throw new InvalidOperationException("La cadena de conexión resultó vacía.");

            if (!cs.EndsWith(";", StringComparison.Ordinal)) cs += ";";

            if (cs.IndexOf("allow user variables", StringComparison.OrdinalIgnoreCase) < 0)
                cs += "allow user variables=true;";

            if (cs.IndexOf("sslmode", StringComparison.OrdinalIgnoreCase) < 0)
                cs += "sslMode=none;";

            return cs;
        }

        static ResolvedDbConnection BuildResolved(string sourceKind, string envVarName, string parametrizacionPath, string connectionString, string siteArg)
        {
            var resolved = new ResolvedDbConnection
            {
                SiteArg = siteArg ?? "",
                SourceKind = sourceKind ?? "",
                EnvVarName = envVarName ?? "",
                ParametrizacionPath = parametrizacionPath ?? "",
                ConnectionString = connectionString ?? ""
            };

            ApplySummaryFromConnectionString(resolved, connectionString);
            return resolved;
        }

        static void ApplySummaryFromConnectionString(ResolvedDbConnection resolved, string connectionString)
        {
            if (resolved == null) return;

            // Best-effort parsing without pulling MySql-specific builders into the server.
            string cs = connectionString ?? "";
            string[] parts = cs.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawPart in parts)
            {
                string part = (rawPart ?? "").Trim();
                if (part.Length == 0) continue;

                int eq = part.IndexOf('=');
                if (eq <= 0) continue;

                string key = part.Substring(0, eq).Trim().ToLowerInvariant();
                string val = part.Substring(eq + 1).Trim();

                if (key == "server" || key == "host" || key == "data source" || key == "datasource")
                    resolved.Server = val;
                else if (key == "port")
                {
                    if (int.TryParse(val, out int p)) resolved.Port = p;
                }
                else if (key == "database" || key == "initial catalog")
                    resolved.Database = val;
                else if (key == "user id" || key == "userid" || key == "uid" || key == "user")
                    resolved.UserId = val;
            }
        }
    }
}
