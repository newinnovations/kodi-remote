using System;
using System.IO;
using System.Text.Json;

namespace KodiRemote
{
    // Persisted Kodi connection details, stored alongside the executable.
    public class KodiSettings
    {
        public string HostUrl { get; set; } = "http://localhost:8080/jsonrpc";
        public string Username { get; set; } = "kodi";
        public string Password { get; set; } = "";

        private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "settings.json");
        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions { WriteIndented = true };

        public static KodiSettings Load(Action<string>? log = null)
        {
            log ??= _ => { };

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
                    log("Settings file did not contain valid settings; using defaults.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                log($"Failed to read settings file '{FilePath}': {ex.Message}");
                BackupCorruptFile(log);
            }

            var defaults = new KodiSettings();
            try
            {
                defaults.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"Failed to save default settings to '{FilePath}': {ex.Message}. Continuing with in-memory defaults.");
            }
            return defaults;
        }

        // Preserves the unreadable file for diagnosis instead of silently overwriting it.
        private static void BackupCorruptFile(Action<string> log)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    string backupPath = $"{FilePath}.corrupt-{DateTime.Now:yyyyMMddHHmmss}.bak";
                    File.Move(FilePath, backupPath, overwrite: true);
                    log($"Backed up unreadable settings file to '{backupPath}'.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"Failed to back up corrupt settings file: {ex.Message}");
            }
        }

        public void Save()
        {
            string json = JsonSerializer.Serialize(this, SerializerOptions);
            File.WriteAllText(FilePath, json);
        }
    }
}

