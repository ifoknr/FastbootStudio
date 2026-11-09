using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FastbootEnhance
{
    /// <summary>
    /// The user's choices, kept in %APPDATA%\Fastboot Studio\settings.json as a flat object of
    /// strings ("language", "theme"). A missing or damaged file reads as no choices made.
    /// </summary>
    static class Settings
    {
        /// <summary>The app's folder under %APPDATA%: the settings and the flash logs.</summary>
        public static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Fastboot Studio");

        static readonly string FilePath = Path.Combine(Folder, "settings.json");

        /// <summary>Set is read-modify-write from several threads (device facts, the UI): one at a time.</summary>
        static readonly object gate = new object();

        static Dictionary<string, string> Load()
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(FilePath))
                    return values;
                string text;
                lock (gate)
                    text = File.ReadAllText(FilePath);
                using (JsonDocument json = JsonDocument.Parse(text))
                {
                    if (json.RootElement.ValueKind != JsonValueKind.Object)
                        return values;
                    foreach (JsonProperty property in json.RootElement.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.String)
                            values[property.Name] = property.Value.GetString();
                    }
                }
            }
            catch (Exception)
            {
                // A damaged settings file must not stop the app from starting.
            }
            return values;
        }

        /// <summary>True once anything was ever saved: the app was used before.</summary>
        public static bool Any()
        {
            return Load().Count > 0;
        }

        public static string Get(string key)
        {
            string value;
            return Load().TryGetValue(key, out value) ? value : null;
        }

        /// <summary>Saves one choice and keeps the others. Returns false when it could not be written.</summary>
        public static bool Set(string key, string value)
        {
            try
            {
                lock (gate)
                {
                    Dictionary<string, string> values = Load();
                    values[key] = value;
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    // Written beside, then moved over: a crash mid-write leaves the old file, not a torn one.
                    string temp = FilePath + ".tmp";
                    File.WriteAllText(temp, JsonSerializer.Serialize(values));
                    File.Move(temp, FilePath, true);
                }
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
