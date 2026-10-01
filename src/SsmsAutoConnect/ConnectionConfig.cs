using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace SsmsAutoConnect
{
    public sealed class ConnectionEntry
    {
        public string Server { get; set; }
        public string Database { get; set; }
        public bool UseWindowsAuth { get; set; } = true;
        public string UserName { get; set; }

        /// <summary>DPAPI-protected, base64 (see PasswordProtector). Never plain text.</summary>
        public string Password { get; set; }

        public override string ToString() => $"{Server}/{(string.IsNullOrEmpty(Database) ? "<default db>" : Database)}";
    }

    /// <summary>
    /// %AppData%\SsmsAutoConnect\connections.xml:
    /// <code>
    /// &lt;Connections&gt;
    ///   &lt;Connection&gt;
    ///     &lt;Server&gt;localhost&lt;/Server&gt;
    ///     &lt;Database&gt;master&lt;/Database&gt;
    ///     &lt;UseWindowsAuth&gt;true&lt;/UseWindowsAuth&gt;
    ///     &lt;UserName /&gt;
    ///     &lt;Password /&gt;
    ///   &lt;/Connection&gt;
    /// &lt;/Connections&gt;
    /// </code>
    /// </summary>
    public static class ConnectionConfig
    {
        public static string ConfigDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SsmsAutoConnect");

        public static string ConfigPath => Path.Combine(ConfigDirectory, "connections.xml");

        public static IReadOnlyList<ConnectionEntry> LoadOrCreateSample()
        {
            if (!File.Exists(ConfigPath))
            {
                Save(new[]
                {
                    new ConnectionEntry { Server = "localhost", Database = "master", UseWindowsAuth = true }
                });
                Log.Info($"Created sample config at {ConfigPath}");
            }
            return Load();
        }

        public static IReadOnlyList<ConnectionEntry> Load()
        {
            XDocument doc = XDocument.Load(ConfigPath);
            return doc.Root.Elements("Connection")
                .Select(e => new ConnectionEntry
                {
                    Server = Text(e, "Server"),
                    Database = Text(e, "Database"),
                    UseWindowsAuth = !bool.TryParse(Text(e, "UseWindowsAuth"), out bool b) || b,
                    UserName = Text(e, "UserName"),
                    Password = Text(e, "Password"),
                })
                .ToList();
        }

        public static void Save(IEnumerable<ConnectionEntry> entries)
        {
            Directory.CreateDirectory(ConfigDirectory);
            var doc = new XDocument(
                new XComment(" Password: DPAPI-encrypted (CurrentUser). Generate with SsmsAutoConnect.Protect.exe. Leave empty for Windows auth. "),
                new XElement("Connections",
                    entries.Select(c => new XElement("Connection",
                        new XElement("Server", c.Server ?? string.Empty),
                        new XElement("Database", c.Database ?? string.Empty),
                        new XElement("UseWindowsAuth", c.UseWindowsAuth ? "true" : "false"),
                        new XElement("UserName", c.UserName ?? string.Empty),
                        new XElement("Password", c.Password ?? string.Empty)))));
            doc.Save(ConfigPath);
        }

        /// <summary>
        /// Adds <paramref name="entry"/>, or, if its server is already listed, updates that entry (database and auth).
        /// Edits the XML in place so other entries, comments and formatting are kept. Returns true if it updated an entry.
        /// </summary>
        public static bool AddOrUpdate(ConnectionEntry entry)
        {
            if (!File.Exists(ConfigPath))
                Save(new ConnectionEntry[0]);

            XDocument doc = XDocument.Load(ConfigPath, LoadOptions.PreserveWhitespace);
            XElement existing = doc.Root.Elements("Connection").FirstOrDefault(e =>
                string.Equals(Text(e, "Server"), entry.Server, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                SetChild(existing, "Database", entry.Database);
                SetChild(existing, "UseWindowsAuth", entry.UseWindowsAuth ? "true" : "false");
                SetChild(existing, "UserName", entry.UserName);
                SetChild(existing, "Password", entry.Password);
            }
            else
            {
                doc.Root.Add(
                    new XText("  "),
                    new XElement("Connection",
                        new XElement("Server", entry.Server),
                        new XElement("Database", entry.Database ?? string.Empty),
                        new XElement("UseWindowsAuth", entry.UseWindowsAuth ? "true" : "false"),
                        new XElement("UserName", entry.UserName ?? string.Empty),
                        new XElement("Password", entry.Password ?? string.Empty)),
                    new XText(Environment.NewLine));
            }
            var settings = new System.Xml.XmlWriterSettings { OmitXmlDeclaration = doc.Declaration == null };
            using (var writer = System.Xml.XmlWriter.Create(ConfigPath, settings))
                doc.Save(writer);
            return existing != null;
        }

        private static void SetChild(XElement parent, string name, string value)
        {
            XElement child = parent.Element(name);
            if (child == null)
                parent.Add(child = new XElement(name));
            child.Value = value ?? string.Empty;
        }

        private static string Text(XElement parent, string name)
        {
            string value = parent.Element(name)?.Value?.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }
}
