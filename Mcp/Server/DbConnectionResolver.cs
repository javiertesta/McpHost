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
        readonly Dictionary<string, string> _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static readonly Regex SiteDirRegex = new Regex("^[A-Za-z]{3}[0-9]{3}P$", RegexOptions.Compiled);

        public DbConnectionResolver(string root)
        {
            _root = root;
        }

        public string ResolveConnectionString(string site)
        {
            string key = string.IsNullOrWhiteSpace(site) ? "__auto__" : site.Trim();
            if (_cache.ContainsKey(key)) return _cache[key];

            string env = ResolveFromEnvironment(site);
            if (!string.IsNullOrWhiteSpace(env))
            {
                string envNormalized = NormalizeConnectionString(env);
                _cache[key] = envNormalized;
                return envNormalized;
            }

            string parametrizacionPath = ResolveParametrizacionPath(site);
            string encrypted = ReadBaseDeDatos(parametrizacionPath);
            string decrypted = DbCrypto.Decrypt(encrypted);
            string normalized = NormalizeConnectionString(decrypted);

            _cache[key] = normalized;
            return normalized;
        }

        static string ResolveFromEnvironment(string site)
        {
            if (!string.IsNullOrWhiteSpace(site))
            {
                string normalized = Regex.Replace(site.ToUpperInvariant(), "[^A-Z0-9]", "_");
                string siteVar = Environment.GetEnvironmentVariable("MCP_DB_CONNECTION_STRING_" + normalized);
                if (!string.IsNullOrWhiteSpace(siteVar)) return siteVar;
            }

            return Environment.GetEnvironmentVariable("MCP_DB_CONNECTION_STRING");
        }

        string ResolveParametrizacionPath(string site)
        {
            if (!string.IsNullOrWhiteSpace(site))
            {
                string requested = site.Trim();

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
    }
}
