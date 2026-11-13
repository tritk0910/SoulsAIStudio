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
        /// <summary>Version of the default dock layout already applied (see <see cref="SasLayout"/>).</summary>
        public int LayoutVersion { get; set; }
        /// <summary>Keep the Windows IME active in the Lua editor (default off: its composition text isn't drawn).</summary>
        public bool AllowImeInEditor { get; set; }

        /// <summary>DSAS sound volume in percent (0–200), restored on launch and for every loaded character.</summary>
        public float SoundVolume { get; set; } = 100;
        /// <summary>Last text of the Combo window's Manual tab (null = show the example).</summary>
        public string? ComboScript { get; set; }
        public bool ComboScriptLoop { get; set; }
        /// <summary>Blend root motion between anims during a TAE Blend (DSAS 4.9.9 behavior). See SasHooks.RootMotionBlending.</summary>
        public bool BlendRootMotion { get; set; } = true;
        /// <summary>Viewport lighting like DSAS 4.9.9 (side light, ambient x1, no SSS wrap). See SasHooks.ApplyLightingPreset.</summary>
        public bool Lighting499 { get; set; } = true;

        /// <summary>TAE graph: while scrubbing the time cursor, scroll so the playhead stays centered (see <see cref="ScrubFollow"/>).</summary>
        public bool CenterPlayheadWhileScrubbing { get; set; } = false; // renamed from FollowPlayheadWhileScrubbing so the new default (off, DSAS 4.9.9 drag) applies

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
