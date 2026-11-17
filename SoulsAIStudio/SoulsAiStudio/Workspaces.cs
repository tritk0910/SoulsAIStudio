#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ImGuiNET;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// Named panel layouts (After Effects "workspaces"): the ImGui dock layout (imgui.ini text) plus which panels are open,
    /// saved as <c>workspaces/&lt;name&gt;.json</c> in the user data folder. A workspace is applied at the start of a frame
    /// (<see cref="SasHooks.BeforeOsdBuild"/>), before any window calls Begin, as DSAS does for its default layouts.
    /// </summary>
    public sealed class Workspaces
    {
        private sealed class Saved
        {
            public string Ini { get; set; } = "";
            public Dictionary<string, bool> Open { get; set; } = new();
        }

        private readonly SasApp _app;
        private readonly IReadOnlyDictionary<string, (Func<bool> Get, Action<bool> Set)> _ourPanels;
        private Saved? _pending;
        private string _newName = "";
        private List<string>? _names;

        public Workspaces(SasApp app, IReadOnlyDictionary<string, (Func<bool> Get, Action<bool> Set)> ourPanels)
        {
            _app = app;
            _ourPanels = ourPanels;
        }

        public static string Folder => Path.Combine(SasSettings.UserDataDir, "workspaces");

        public IReadOnlyList<string> Names => _names ??= LoadNames();

        private static List<string> LoadNames()
        {
            try
            {
                return Directory.Exists(Folder)
                    ? Directory.GetFiles(Folder, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)!).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
                    : new List<string>();
            }
            catch (Exception) { return new List<string>(); }
        }

        /// <summary>A name usable as a file name (no path characters); null when nothing is left.</summary>
        public static string? CleanName(string name)
        {
            var bad = Path.GetInvalidFileNameChars();
            string s = new string(name.Trim().Where(c => !bad.Contains(c)).ToArray()).Trim().TrimEnd('.');
            return s.Length == 0 ? null : s;
        }

        private static string PathOf(string name) => Path.Combine(Folder, name + ".json");

        public void Save(string name)
        {
            string? clean = CleanName(name);
            if (clean == null) return;
            var saved = new Saved { Ini = ImGui.SaveIniSettingsToMemory() };
            DSAnimStudio.ImguiOSD.OSD.ForAllStaticWindows(w =>
            {
                if (w.GetSaveOpenStateType() == DSAnimStudio.ImguiOSD.Window.SaveOpenStateTypes.SaveAlways)
                    saved.Open["dsas:" + w.NewImguiWindowTitle] = w.IsOpen;
            });
            foreach (var (key, panel) in _ourPanels) saved.Open[key] = panel.Get();
            Directory.CreateDirectory(Folder);
            Core.IO.AtomicFile.WriteAllText(PathOf(clean), JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
            _names = null;
            _app.Notify($"Workspace \"{clean}\" saved");
        }

        public void RequestLoad(string name)
        {
            try
            {
                var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(PathOf(name)));
                if (saved == null || saved.Ini.Length == 0) return;
                _pending = saved;
            }
            catch (Exception ex) { _app.ReportError($"Could not read workspace \"{name}\"", ex); }
        }

        public void Delete(string name)
        {
            try { File.Delete(PathOf(name)); } catch (Exception ex) { _app.ReportError("Could not delete workspace", ex); }
            _names = null;
        }

        /// <summary>Start of a frame: loads the requested layout, then opens/closes panels as saved.</summary>
        public void ApplyPending()
        {
            var saved = _pending;
            if (saved == null) return;
            _pending = null;
            ImGui.LoadIniSettingsFromMemory(saved.Ini);
            DSAnimStudio.ImguiOSD.OSD.ForAllStaticWindows(w =>
            {
                if (saved.Open.TryGetValue("dsas:" + w.NewImguiWindowTitle, out bool open)) w.IsOpen = open;
            });
            foreach (var (key, panel) in _ourPanels)
                if (saved.Open.TryGetValue(key, out bool open)) panel.Set(open);
            _app.Layout.WorkspaceLoaded();
        }

        /// <summary>The "Workspaces" menu items (Window menu and Souls AI > Layout).</summary>
        public void MenuItems()
        {
            if (ImGui.MenuItem("Lock Panel Layout", "", _app.Settings.LockPanelLayout))
            {
                _app.Settings.LockPanelLayout = !_app.Settings.LockPanelLayout;
                _app.Settings.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Off: drag any panel (Viewport, Animations, Parameters, Graph, Lua Editor…) by its tab to dock it\n" +
                                 "anywhere, split a panel, or float it. On: DSAS's panels stay where they are.");
            if (ImGui.BeginMenu("Workspaces"))
            {
                if (Names.Count == 0) ImGui.TextDisabled("No saved workspaces yet");
                foreach (string n in Names)
                    if (ImGui.MenuItem(n)) RequestLoad(n);
                ImGui.Separator();
                if (ImGui.BeginMenu("Save Current Layout As..."))
                {
                    ImGui.SetNextItemWidth(200);
                    if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
                    bool enter = ImGui.InputTextWithHint("##sas_ws_name", "Workspace name", ref _newName, 64, ImGuiInputTextFlags.EnterReturnsTrue);
                    // Typing here must not reach DSAS's graph shortcuts (T = Add Tag, etc.).
                    if (ImGui.IsItemActive()) DSAnimStudio.ImguiOSD.OSD.AuxFocus = Math.Max(DSAnimStudio.ImguiOSD.OSD.AuxFocus, 2);
                    string? clean = CleanName(_newName);
                    bool exists = clean != null && Names.Contains(clean, StringComparer.OrdinalIgnoreCase);
                    ImGui.SameLine();
                    if ((ImGui.Button(exists ? "Overwrite" : "Save") || enter) && clean != null)
                    {
                        Save(clean);
                        _newName = "";
                        ImGui.CloseCurrentPopup();
                    }
                    ImGui.EndMenu();
                }
                if (ImGui.BeginMenu("Delete", Names.Count > 0))
                {
                    foreach (string n in Names.ToList())
                        if (ImGui.MenuItem(n)) Delete(n);
                    ImGui.EndMenu();
                }
                ImGui.EndMenu();
            }
        }
    }
}
