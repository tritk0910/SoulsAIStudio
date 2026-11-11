#nullable enable
using System.IO;
using System.Numerics;
using ImGuiNET;

namespace SoulsAiStudio.App
{
    /// <summary>Start screen: New Project / Open / Recent (ui-spec §2a).</summary>
    public sealed class StartScreen
    {
        private readonly SasApp _app;
        public bool IsOpen;

        public StartScreen(SasApp app) => _app = app;

        /// <returns>True when the window has focus.</returns>
        public bool Build()
        {
            if (!IsOpen) return false;
            var vp = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(vp.WorkPos + vp.WorkSize * 0.5f, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
            ImGui.SetNextWindowSize(new Vector2(620, 420), ImGuiCond.Appearing);
            bool focused = false;
            bool visible = Theme.BeginOpaque("Souls AI Studio##sas_start", ref IsOpen, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking);
            try
            {
            if (visible)
            {
                focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
                ImGui.TextColored(Theme.Accent, "Souls AI Studio");
                ImGui.TextDisabled("Sekiro AI Lua editor (built on DS Anim Studio)");
                ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();

                if (ImGui.Button("New Project...", new Vector2(180, 36))) _app.ShowNewProjectWizard();
                ImGui.SameLine();
                if (ImGui.Button("Open Project...", new Vector2(180, 36))) _app.BrowseAndOpenProject();

                ImGui.Spacing();
                ImGui.Text("Recent projects");
                ImGui.BeginChild("##recent", new Vector2(0, -32), true);
                if (_app.Recent.Paths.Count == 0) ImGui.TextDisabled("No recent projects yet.");
                foreach (string p in _app.Recent.Paths)
                {
                    bool exists = File.Exists(p);
                    if (!exists) ImGui.PushStyleColor(ImGuiCol.Text, Theme.Dim);
                    if (ImGui.Selectable($"{Path.GetFileNameWithoutExtension(p)}##{p}", false, ImGuiSelectableFlags.AllowDoubleClick) && exists)
                        _app.OpenProject(p);
                    if (!exists) ImGui.PopStyleColor();
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(exists ? p : p + "\n(file not found)");
                    ImGui.SameLine(260);
                    ImGui.TextDisabled(Path.GetDirectoryName(p) ?? "");
                }
                ImGui.EndChild();

                bool show = _app.Settings.ShowStartScreenOnLaunch;
                if (ImGui.Checkbox("Show this screen on launch", ref show))
                {
                    _app.Settings.ShowStartScreenOnLaunch = show;
                    _app.Settings.Save();
                }
            }
            }
            finally { ImGui.End(); }
            return focused;
        }
    }

    internal static class Theme
    {
        /// <summary>Opaque window background (DSAS makes unfocused windows 65% transparent).</summary>
        public static readonly Vector4 WindowBg = new(0.15f, 0.15f, 0.15f, 1f);

        /// <summary><c>ImGui.Begin</c> with a fully opaque background.</summary>
        public static bool BeginOpaque(string name, ref bool open, ImGuiWindowFlags flags)
        {
            ImGui.PushStyleColor(ImGuiCol.WindowBg, WindowBg);
            try { return ImGui.Begin(name, ref open, flags); }
            finally { ImGui.PopStyleColor(); }
        }

        public static readonly Vector4 Accent = new(0.95f, 0.66f, 0.25f, 1f);
        public static readonly Vector4 Dim = new(0.55f, 0.55f, 0.55f, 1f);
        public static readonly Vector4 Ok = new(0.45f, 0.85f, 0.45f, 1f);
        public static readonly Vector4 Warn = new(0.95f, 0.8f, 0.3f, 1f);
        public static readonly Vector4 Error = new(0.95f, 0.4f, 0.4f, 1f);
        public static readonly Vector4 GroupHeader = new(0.6f, 0.75f, 0.95f, 1f);
    }
}
