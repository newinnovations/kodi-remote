using System;
using System.IO;
using System.Text.Json;

namespace KodiListenerGui
{
    // Persisted Kodi connection details, stored alongside the executable.
    public class KodiSettings
    {
        public string HostUrl { get; set; } = "http://localhost:8080/jsonrpc";
        public string Username { get; set; } = "kodi";
        public string Password { get; set; } = "";

        private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "settings.json");
        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions { WriteIndented = true };

        public static KodiSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    string json = File.ReadAllText(FilePath);
                    var settings = JsonSerializer.Deserialize<KodiSettings>(json);
                    if (settings != null)
                    {
                        return settings;
                    }
                }
            }
            catch
            {
                // Fall through to defaults if the settings file is missing or malformed.
            }

            var defaults = new KodiSettings();
            defaults.Save();
            return defaults;
        }

        public void Save()
        {
            string json = JsonSerializer.Serialize(this, SerializerOptions);
            File.WriteAllText(FilePath, json);
        }
    }
}
