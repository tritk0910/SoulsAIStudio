#nullable enable
using System;
using System.IO;
using DSAnimStudio;
using SoulsAiStudio.Core.IO;
using SoulsAiStudio.Core.Project;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// The only place our UI calls into DSAS internals (zzz_*, Main.*) for viewing characters
    /// (CLAUDE.md §6 convention 2). Uses the same entry point as DSAS's URI loader / recent-files menu.
    /// </summary>
    public sealed class DsasBridge
    {
        /// <summary>Main anibnd currently open in DSAS, or null.</summary>
        public string? LoadedAnibnd => Safe(() => zzz_DocumentManager.CurrentDocument?.EditorScreen?.NewFileContainerName);

        public bool IsLoaded(CharacterFiles files) =>
            LoadedAnibnd is { } p && string.Equals(Path.GetFullPath(p), Path.GetFullPath(files.AnibndPath), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Opens the character's anibnd + chrbnd in DSAS (model, skeleton, animation list, TAE).
        /// For files in the mod folder we pre-write DSAS's <c>_DSAS_WORKSPACE.json</c> next to the anibnd (game dir +
        /// mod dir) so DSAS doesn't ask for the game folder. We never write that file into the game folder.
        /// </summary>
        public void LoadCharacter(CharacterFiles files, string gameDir, string modDir, Action<string>? onLoaded = null)
        {
            var doc = zzz_DocumentManager.CurrentDocument ?? throw new InvalidOperationException("DSAS has no document to load into.");
            _pendingAnibnd = Path.GetFullPath(files.AnibndPath);
            // DSAS needs Oodle next to its exe for KRAK files; with the game known (Sekiro) its own helper copies the
            // user's local oo2core from the game folder without the "More Info Needed" prompt. Local only, never shipped.
            if (!Main.AnyOodleExists()) Main.TryCopyOodleFromInterroot(gameDir, files.AnibndPath, SoulsAssetPipeline.SoulsGames.SDT);
            string anibndDir = Path.GetDirectoryName(Path.GetFullPath(files.AnibndPath))!;
            string workspaceJson = Path.Combine(anibndDir, "_DSAS_WORKSPACE.json");
            bool canWriteWorkspace = WritePolicy.WhyBlocked(workspaceJson, new[] { gameDir }) == null;
            if (canWriteWorkspace && !File.Exists(workspaceJson))
                doc.GameRoot.SetInterrootPickerPathsManually(anibndDir, gameDir, modDir, loadLooseParams: false);
            if (canWriteWorkspace || File.Exists(workspaceJson))
                doc.GameRoot.SuppressNextInterrootBrowsePrompt = true;
            if (doc.EditorScreen != null) doc.EditorScreen.SuppressNextModelOverridePrompt = true;

            var container = new DSAProj.TaeContainerInfo.ContainerAnibnd(files.AnibndPath, files.ChrbndPath ?? "");
            zzz_DocumentManager.RequestFileOpenRecent = true;
            zzz_DocumentManager.RequestFileOpenRecent_SelectedFile = container;
            zzz_DocumentManager.RequestFileOpenRecent_AfterOpenAction += _ => onLoaded?.Invoke(files.ChrId);
        }

        private string? _pendingAnibnd;

        /// <summary>
        /// DSAS creates a fresh document for each open, so the "skip workspace/model prompts" flags must be set on
        /// that new document. Only applies to loads we requested (workspace json already prepared by us).
        /// </summary>
        public void BeforeOpenRecent(zzz_DocumentIns doc, DSAProj.TaeContainerInfo container)
        {
            if (_pendingAnibnd == null) return;
            string? main = container?.GetMainBinderName();
            if (main == null || !string.Equals(Path.GetFullPath(main), _pendingAnibnd, StringComparison.OrdinalIgnoreCase)) return;
            _pendingAnibnd = null;
            if (File.Exists(Path.Combine(Path.GetDirectoryName(main)!, "_DSAS_WORKSPACE.json")))
                doc.GameRoot.SuppressNextInterrootBrowsePrompt = true;
            if (doc.EditorScreen != null) doc.EditorScreen.SuppressNextModelOverridePrompt = true;
        }

        /// <summary>Selects animation <paramref name="animId"/> (e.g. 3002 → a000_003002) in DSAS's timeline/viewport.</summary>
        public bool GotoAnim(long animId)
        {
            var doc = zzz_DocumentManager.CurrentDocument;
            if (doc?.EditorScreen == null || !doc.EditorScreen.IsFileOpen) return false;
            return doc.EditorScreen.GotoAnimID(SplitAnimID.FromFullID(doc.GameRoot, animId), true, false, out var found) && found != null;
        }

        private static T? Safe<T>(Func<T?> f) where T : class
        {
            try { return f(); } catch (Exception) { return null; }
        }
    }
}
