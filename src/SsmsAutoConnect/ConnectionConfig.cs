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

        /// <summary>
        /// Optional custom connection color (Connect dialog: "Use custom color"), "#RRGGBB" or a color name.
        /// Null/empty = no custom color.
        /// </summary>
        public string Color { get; set; }

        /// <summary>Parses <see cref="Color"/>; null if empty or invalid.</summary>
        public System.Drawing.Color? TryGetColor()
        {
            if (string.IsNullOrWhiteSpace(Color))
                return null;
            try
            {
                string text = Color.Trim();
                if (System.Text.RegularExpressions.Regex.IsMatch(text, "^[0-9A-Fa-f]{6}$"))
                    text = "#" + text;   // "FF8000" → "#FF8000"
                System.Drawing.Color c = System.Drawing.ColorTranslator.FromHtml(text);
                return c.IsEmpty ? (System.Drawing.Color?)null : c;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string FormatColor(System.Drawing.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        /// <summary>Server + login: entries with the same key are the same connection.</summary>
        public string Key => MakeKey(Server, UseWindowsAuth ? null : UserName);

        /// <summary>Identity of a connection: server + login (null/empty login = Windows auth). Compare case-insensitively.</summary>
        public static string MakeKey(string server, string sqlLogin) =>
            $"{server?.Trim()}|{(string.IsNullOrEmpty(sqlLogin) ? "<windows>" : sqlLogin.Trim())}";

        public override string ToString() =>
            $"{Server}/{(string.IsNullOrEmpty(Database) ? "<default db>" : Database)} ({(UseWindowsAuth ? "Windows" : UserName)})";
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
    ///     &lt;Color&gt;#FF8000&lt;/Color&gt;   (optional custom connection color)
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
            return doc.Root.Elements("Connection").Select(Parse).ToList();
        }

        private static ConnectionEntry Parse(XElement e) => new ConnectionEntry
        {
            Server = Text(e, "Server"),
            Database = Text(e, "Database"),
            UseWindowsAuth = !bool.TryParse(Text(e, "UseWindowsAuth"), out bool b) || b,
            UserName = Text(e, "UserName"),
            Password = Text(e, "Password"),
            Color = Text(e, "Color"),
        };

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
                        new XElement("Password", c.Password ?? string.Empty),
                        string.IsNullOrEmpty(c.Color) ? null : new XElement("Color", c.Color)))));
            doc.Save(ConfigPath);
        }

        /// <summary>The configured entry with the same server + login as <paramref name="entry"/>, or null.</summary>
        public static ConnectionEntry Find(ConnectionEntry entry)
        {
            if (!File.Exists(ConfigPath))
                return null;
            return Load().FirstOrDefault(e => string.Equals(e.Key, entry.Key, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Adds <paramref name="entry"/>, or, if the same server + login is already listed, updates that entry's database
        /// (and password). Edits the XML in place so other entries, comments and formatting are kept.
        /// Returns true if it updated an entry.
        /// </summary>
        public static bool AddOrUpdate(ConnectionEntry entry)
        {
            if (!File.Exists(ConfigPath))
                Save(new ConnectionEntry[0]);

            XDocument doc = XDocument.Load(ConfigPath, LoadOptions.PreserveWhitespace);
            XElement existing = doc.Root.Elements("Connection").FirstOrDefault(e =>
                string.Equals(Parse(e).Key, entry.Key, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                SetChild(existing, "Database", entry.Database);
                if (!entry.UseWindowsAuth && !string.IsNullOrEmpty(entry.Password))
                    SetChild(existing, "Password", entry.Password);
                if (string.IsNullOrEmpty(entry.Color))
                    existing.Element("Color")?.Remove();
                else
                    SetChild(existing, "Color", entry.Color);
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
                        new XElement("Password", entry.Password ?? string.Empty),
                        string.IsNullOrEmpty(entry.Color) ? null : new XElement("Color", entry.Color)),
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
