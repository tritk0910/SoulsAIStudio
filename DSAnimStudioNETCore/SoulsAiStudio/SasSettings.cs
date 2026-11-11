#nullable enable
using System;
using System.IO;
using System.Text.Json;
using SoulsAiStudio.Core.IO;
using SoulsAiStudio.Core.Lua.Decompile;

namespace SoulsAiStudio.App
{
    /// <summary>Per-user app settings (<c>%AppData%\SoulsAiStudio\settings.json</c>).</summary>
    public sealed class SasSettings
    {
        public string DecompilerPath { get; set; } = LuaDecompiler.DefaultExePath;
        public string LastGameDir { get; set; } = "";
        public string LastModDir { get; set; } = "";
        public string LastProjectFolder { get; set; } = "";
        public bool ShowStartScreenOnLaunch { get; set; } = true;

        /// <summary>
        /// Per-user data folder. <c>SOULSAISTUDIO_USERDATA</c> overrides it (used by UI tests so they never touch the
        /// real profile; note <see cref="Environment.GetFolderPath"/> ignores the APPDATA variable).
        /// </summary>
        public static string UserDataDir =>
            Environment.GetEnvironmentVariable("SOULSAISTUDIO_USERDATA") is { Length: > 0 } dir ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoulsAiStudio");

        public static string StorePath => Path.Combine(UserDataDir, "settings.json");

        public static SasSettings Load()
        {
            try
            {
                if (File.Exists(StorePath))
                    return JsonSerializer.Deserialize<SasSettings>(File.ReadAllText(StorePath)) ?? new SasSettings();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            return new SasSettings();
        }

        public void Save()
        {
            try { AtomicFile.WriteAllText(StorePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        /// <summary>Best guess for the Sekiro install folder (default Steam library).</summary>
        public static string GuessGameDir()
        {
            string steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Steam", "steamapps", "common", "Sekiro");
            return Directory.Exists(steam) ? steam : "";
        }
    }
}
