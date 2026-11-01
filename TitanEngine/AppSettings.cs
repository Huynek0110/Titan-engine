using System;
using System.IO;
using System.Text.Json;

namespace TitanEngine
{
    /// <summary>
    /// Tiny persistent UI settings (last used folders) stored as JSON at
    /// <c>%APPDATA%\TitanEngine\settings.json</c>. All I/O is best-effort: a missing or corrupt
    /// file simply yields defaults, and a failed save never blocks or crashes the app.
    /// This is UI-preference persistence only - it never touches render jobs or user media.
    /// </summary>
    public static class AppSettings
    {
        private static readonly string SettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TitanEngine",
            "settings.json");

        private sealed class SettingsDto
        {
            public string? LastSourceFolder { get; set; }
            public string? LastOutputFolder { get; set; }
        }

        public static string? LastSourceFolder { get; private set; }
        public static string? LastOutputFolder { get; private set; }

        public static void Load()
        {
            try
            {
                if (!File.Exists(SettingsFilePath))
                    return;

                SettingsDto? dto = JsonSerializer.Deserialize<SettingsDto>(
                    File.ReadAllText(SettingsFilePath));
                LastSourceFolder = ValidFolder(dto?.LastSourceFolder) ? dto!.LastSourceFolder : null;
                LastOutputFolder = ValidFolder(dto?.LastOutputFolder) ? dto!.LastOutputFolder : null;
            }
            catch
            {
                // Corrupt or unreadable settings must never block startup - just use defaults.
                LastSourceFolder = null;
                LastOutputFolder = null;
            }
        }

        /// <summary>Remembers the folder of the last chosen source file and saves.</summary>
        public static void RememberSourceFolder(string? fullPath)
        {
            try
            {
                string? dir = Path.GetDirectoryName(fullPath);
                if (ValidFolder(dir))
                {
                    LastSourceFolder = dir;
                    Save();
                }
            }
            catch
            {
            }
        }

        /// <summary>Remembers the last chosen output folder and saves.</summary>
        public static void RememberOutputFolder(string? folder)
        {
            try
            {
                if (ValidFolder(folder))
                {
                    LastOutputFolder = folder;
                    Save();
                }
            }
            catch
            {
            }
        }

        private static bool ValidFolder(string? folder)
            => !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder);

        private static void Save()
        {
            try
            {
                string? dir = Path.GetDirectoryName(SettingsFilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var dto = new SettingsDto
                {
                    LastSourceFolder = LastSourceFolder,
                    LastOutputFolder = LastOutputFolder
                };
                File.WriteAllText(SettingsFilePath,
                    JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // Saving preferences is best-effort; never let it interrupt the user.
            }
        }
    }
}
