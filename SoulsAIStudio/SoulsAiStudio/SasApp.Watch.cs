#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using SoulsAiStudio.Core.IO;
using SoulsAiStudio.Core.Watch;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// File watcher (CLAUDE.md §5 P0 item 6): luabnds of the project's scripts, the loaded character's anibnd/chrbnd and
    /// gameparam. Changes made by other tools are reloaded (clean script tabs) or offered (banner); our own writes are
    /// ignored (ProjectSession / DSAS save tell the watcher). Everything here runs on the UI thread.
    /// </summary>
    public sealed partial class SasApp
    {
        private FileChangeWatcher? _fileWatch;
        private string _watchKey = "";
        private DateTime _nextWatchRefresh;
        private readonly List<DiskChange> _diskChanges = new();

        /// <summary>A non-script file that changed on disk and waits for the user (banner).</summary>
        private sealed class DiskChange
        {
            public WatchKind Kind;
            public string Tag = "";
            public List<string> Files = new();
            public bool ConfirmDiscard;
        }

        /// <summary>Tell the watcher about files we are about to write (null when not watching).</summary>
        public IOwnWriteTracker? FileWatch => _fileWatch;

        private void StartFileWatch()
        {
            StopFileWatch();
            if (Session == null || !Settings.WatchFiles) return;
            try
            {
                _fileWatch = new FileChangeWatcher();
                Session.WriteTracker = _fileWatch;
            }
            catch (Exception ex) { ReportError("Could not watch files for external changes", ex); }
        }

        private void StopFileWatch()
        {
            if (Session != null) Session.WriteTracker = null;
            _fileWatch?.Dispose();
            _fileWatch = null;
            _watchKey = "";
            _diskChanges.Clear();
        }

        /// <summary>Once per frame while a project is open: keeps the watched set current and handles changes.</summary>
        private void PollFileWatch()
        {
            if (_fileWatch == null || Session == null) return;
            if (DateTime.UtcNow >= _nextWatchRefresh)
            {
                _nextWatchRefresh = DateTime.UtcNow.AddSeconds(1);
                RefreshWatchSpecs();
            }
            var changes = _fileWatch.Drain();
            foreach (var group in changes.GroupBy(c => (c.Spec.Kind, Tag: c.Spec.Tag.ToLowerInvariant())))
            {
                try { HandleDiskChange(group.Key.Kind, group.First().Spec.Tag, group.SelectMany(c => c.Files).Distinct(StringComparer.OrdinalIgnoreCase).ToList()); }
                catch (Exception ex) { ReportError($"Reloading after an external change ({group.First().Spec.Tag})", ex); }
            }
            BuildDiskChangeBanner();
        }

        private void RefreshWatchSpecs()
        {
            if (_fileWatch == null || Project == null) return;
            string? anibnd = Bridge.LoadedAnibnd;
            string key = string.Join("|", Project.Scripts.Select(s => s.Luabnd).Distinct(StringComparer.OrdinalIgnoreCase)) + "#" + anibnd;
            if (key == _watchKey) return;
            _watchKey = key;
            try
            {
                string? chrbnd = anibnd != null ? ResolveCharacterId(ProjectWatchSet.ChrId(anibnd))?.ChrbndPath : null;
                _fileWatch.SetSpecs(ProjectWatchSet.Build(Project, Session!.Catalog, anibnd, chrbnd));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                System.Diagnostics.Debug.WriteLine(ex); // retried when the key changes
            }
        }

        private void HandleDiskChange(WatchKind kind, string tag, List<string> files)
        {
            switch (kind)
            {
                case WatchKind.Luabnd:
                    _editor.OnLuabndChangedOnDisk(tag);
                    break;
                case WatchKind.Anibnd:
                case WatchKind.Chrbnd:
                    // Only the character on screen matters (the watched set follows it, but a change may arrive late).
                    if (Bridge.LoadedAnibnd is not { } loaded || !string.Equals(ProjectWatchSet.ChrId(loaded), tag, StringComparison.OrdinalIgnoreCase)) return;
                    AddDiskChange(kind, tag, files);
                    break;
                case WatchKind.GameParam:
                    if (Bridge.DsasAutoReloadsParams && Bridge.CanReloadParams)
                        Notify("gameparam.parambnd.dcx changed on disk (DSAS reloads params automatically).");
                    else AddDiskChange(kind, tag, files);
                    break;
            }
        }

        private void AddDiskChange(WatchKind kind, string tag, List<string> files)
        {
            // anibnd + chrbnd of one character = one entry (one Reload).
            WatchKind group = kind == WatchKind.Chrbnd ? WatchKind.Anibnd : kind;
            var existing = _diskChanges.FirstOrDefault(d => d.Kind == group && string.Equals(d.Tag, tag, StringComparison.OrdinalIgnoreCase));
            if (existing == null) _diskChanges.Add(existing = new DiskChange { Kind = group, Tag = tag });
            foreach (string f in files)
                if (!existing.Files.Contains(f, StringComparer.OrdinalIgnoreCase)) existing.Files.Add(f);
        }

        private void BuildDiskChangeBanner()
        {
            if (_diskChanges.Count == 0) return;
            var vp = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(vp.WorkPos + new Vector2(vp.WorkSize.X * 0.5f, 40), ImGuiCond.Appearing, new Vector2(0.5f, 0));
            bool open = true;
            if (Theme.BeginOpaque("Changed on disk##sas_diskchange", ref open,
                    ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking))
            {
                DiskChange? remove = null;
                for (int i = 0; i < _diskChanges.Count; i++)
                {
                    var d = _diskChanges[i];
                    ImGui.PushID(i);
                    try
                    {
                        if (i > 0) ImGui.Separator();
                        if (BuildDiskChangeItem(d)) remove = d;
                    }
                    finally { ImGui.PopID(); }
                }
                if (remove != null) _diskChanges.Remove(remove);
            }
            ImGui.End();
            if (!open) _diskChanges.Clear();
        }

        /// <returns>True when the item is done (reloaded or dismissed).</returns>
        private bool BuildDiskChangeItem(DiskChange d)
        {
            string names = string.Join(", ", d.Files.Select(Path.GetFileName));
            ImGui.TextColored(Theme.Warn, $"{names} changed on disk.");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(string.Join("\n", d.Files).Replace("%", "%%") + "\n\nChanged by another program (DSAS, WitchyBND, Smithbox...).");

            if (d.Kind == WatchKind.GameParam)
            {
                if (Bridge.CanReloadParams)
                {
                    if (ImGui.Button("Reload params"))
                    {
                        try { Bridge.ReloadParams(); Notify("Reloading params (GameParam + FMGs)..."); }
                        catch (Exception ex) { ReportError("Could not reload params", ex); }
                        return true;
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Same as DSAS File > Reload GameParam and FMGs: SpEffect names, attack params... are read again.");
                }
                else ImGui.TextDisabled("Params are read again when a character is loaded.");
                ImGui.SameLine();
                return ImGui.Button("Dismiss");
            }

            // Character (anibnd / chrbnd).
            bool modified = Bridge.TaeModified;
            if (d.ConfirmDiscard)
            {
                ImGui.TextColored(Theme.Error, "The animations have unsaved changes here. Reload the file from disk without them?");
                if (ImGui.Button("Reload, discard my TAE changes")) { ReloadCharacterFromDisk(d.Tag); return true; }
                ImGui.SameLine();
                if (ImGui.Button("Cancel")) d.ConfirmDiscard = false;
                return false;
            }
            if (modified) ImGui.TextColored(Theme.Error, "The animations (TAE) also have unsaved changes in this app.");
            if (ImGui.Button($"Reload {d.Tag}"))
            {
                if (modified) d.ConfirmDiscard = true;
                else { ReloadCharacterFromDisk(d.Tag); return true; }
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Open the character again from disk (same as Reload in the Project panel).");
            ImGui.SameLine();
            bool dismiss = ImGui.Button("Dismiss");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(modified ? "Keep working on what is loaded. Saving will overwrite the file on disk." : "Keep what is loaded.");
            return dismiss;
        }

        /// <summary>Reloads the loaded character from disk, the way the Project panel's Reload button does.</summary>
        private void ReloadCharacterFromDisk(string chrId)
        {
            if (Project == null) return;
            if (_editor.ActiveScript is { } script && string.Equals(ResolveCharacter(script)?.ChrId, chrId, StringComparison.OrdinalIgnoreCase))
            {
                LoadCharacter(script);
                return;
            }
            var files = ResolveCharacterId(chrId);
            if (files == null) { Notify($"{chrId}.anibnd.dcx not found in the mod or the game chr folder."); return; }
            try
            {
                files = OpenableFiles(files) ?? throw new FileNotFoundException($"{chrId}.anibnd.dcx not found in the game chr folder.");
                Bridge.LoadCharacter(files, Project.GameDir, Project.ModDir, chr => Notify($"Reloaded {chr} from disk."));
                Notify($"Reloading {chrId}...");
            }
            catch (Exception ex) { ReportError($"Could not reload {chrId}", ex); }
        }

        private void WatchFilesMenuItem()
        {
            if (ImGui.MenuItem("Watch Files for External Changes", "", Settings.WatchFiles))
            {
                Settings.WatchFiles = !Settings.WatchFiles;
                Settings.Save();
                if (Settings.WatchFiles) StartFileWatch(); else StopFileWatch();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Notice when the project's luabnds, the loaded anibnd/chrbnd or gameparam are changed by another program\n" +
                                 "(VSCode, WitchyBND, DSAS, Smithbox...). Script tabs without unsaved edits reload by themselves;\n" +
                                 "tabs with unsaved edits show a banner (Keep mine / Load theirs / Show diff).");
        }
    }
}
