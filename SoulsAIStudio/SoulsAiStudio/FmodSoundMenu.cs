using System;
using System.Linq;
using DSAnimStudio;
using DSAnimStudio.ImguiOSD;
using ImGuiNET;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// The "FMOD Sound" top menu of DSAS 4.9.9 (Retry Initialization / Reload All Sounds / STOP ALL SOUNDS /
    /// Load Additional Sounds / Footstep Material). DSAS 5.0 kept the FMOD engine but moved these into the Sound
    /// window (bank load order list) and dropped the menu; this rebuilds it on top of the 5.0 sound manager.
    /// </summary>
    internal static class FmodSoundMenu
    {
        public static void Build(ref bool isAnyMenuExpanded)
        {
            var doc = zzz_DocumentManager.CurrentDocument;
            var sm = doc?.SoundManager;
            // Only FMOD games (Sekiro, DS3, DS1, BB). Wwise/MagicOrchestra games keep DSAS's Sound window.
            if (sm != null && sm.EngineType is not (zzz_SoundManagerIns.EngineTypes.FMOD or zzz_SoundManagerIns.EngineTypes.None)) return;
            if (!ImGui.BeginMenu("FMOD Sound")) return;
            isAnyMenuExpanded = true;
            try
            {
                bool fileOpen = doc?.EditorScreen?.IsFileOpen == true;
                bool fmodReady = sm != null && sm.EngineType == zzz_SoundManagerIns.EngineTypes.FMOD && NewFmodIns.EventSys != null;
                var interactor = doc?.EditorScreen?.Graph?.ViewportInteractor;

                bool enabled = !zzz_SoundManagerIns.SOUND_DISABLED;
                if (ImGui.MenuItem("Retry Initialization", "", false, !enabled || !fmodReady))
                {
                    zzz_SoundManagerIns.SOUND_DISABLED = false;
                    interactor?.LoadFmodSoundsForCurrentModel();
                    doc?.Fmod?.InitFmodEventSysForThisDocument();
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Turns the audio system back on and reloads the sound banks of the loaded character.");

                ImGui.Separator();

                if (ImGui.MenuItem("Reload All Sounds", "", false, fmodReady && fileOpen && enabled))
                {
                    sm!.PurgeLoadedAssets();
                    interactor?.LoadFmodSoundsForCurrentModel();
                    doc!.Fmod?.InitFmodEventSysForThisDocument();
                }

                ImGui.Separator();

                if (ImGui.MenuItem("STOP ALL SOUNDS", "Escape", false, sm != null))
                    sm!.StopAllSounds();

                if (ImGui.BeginMenu("Load Additional Sounds", fmodReady && fileOpen && enabled))
                {
                    try { AdditionalSounds(doc!, sm!); }
                    finally { ImGui.EndMenu(); }
                }

                ImGui.Separator();

                if (ImGui.BeginMenu("Footstep Material", doc?.Fmod != null))
                {
                    try
                    {
                        var fmod = doc!.Fmod;
                        if (fmod.FloorMaterialNames.Count == 0) ImGui.TextDisabled("(no materials loaded - open a character first)");
                        foreach (var mat in fmod.FloorMaterialNames.OrderBy(m => m.Key))
                            if (ImGui.MenuItem($"Material {mat.Key:D2}", mat.Value, mat.Key == fmod.FloorMaterial))
                                fmod.FloorMaterial = mat.Key;
                    }
                    finally { ImGui.EndMenu(); }
                }

                ImGui.Separator();
                if (ImGui.MenuItem("Sound Window (volume, bank load order)...")) OSD.WindowSound.IsOpen = true;
            }
            finally { ImGui.EndMenu(); }
        }

        /// <summary>Every .fev bank of the game/mod; "(Loaded)" for those in the lookup list. Click adds it, as in 4.9.9.</summary>
        private static void AdditionalSounds(zzz_DocumentIns doc, zzz_SoundManagerIns sm)
        {
            string[] all, loaded;
            lock (sm._lock_BankNameArray) all = sm.BankNameArray?.ToArray() ?? Array.Empty<string>();
            lock (sm._lock_LookupBanks) loaded = sm.LookupBanks?.ToArray() ?? Array.Empty<string>();
            if (all.Length == 0) { ImGui.TextDisabled("(no sound banks found in the game/mod sound folder)"); return; }
            foreach (string bank in all)
            {
                bool isLoaded = loaded.Contains(bank, StringComparer.OrdinalIgnoreCase);
                if (ImGui.MenuItem(bank, isLoaded ? "(Loaded)" : "", false, !isLoaded))
                {
                    sm.RegisterAdditionalSoundBank(bank);
                    doc.Fmod?.InitFmodEventSysForThisDocument();
                }
            }
        }
    }
}
