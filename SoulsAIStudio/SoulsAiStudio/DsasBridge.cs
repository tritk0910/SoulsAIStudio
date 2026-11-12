#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using DSAnimStudio;
using DSAnimStudio.TaeEditor;
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
            if (!doc.EditorScreen.GotoAnimID(SplitAnimID.FromFullID(doc.GameRoot, animId), true, false, out var found) || found == null)
                return false;
            doc.EditorScreen.Graph?.ViewportInteractor?.CancelCombo();
            var cursor = doc.EditorScreen.PlaybackCursor;
            if (cursor != null) { cursor.CurrentTime = 0; cursor.IsPlaying = true; } // play right away (user request)
            return true;
        }

        /// <summary>One played combo step and where it was cut.</summary>
        public sealed record ComboPlan(int AnimId, float EndFrame, float TotalFrames, string CutReason, bool Missing);

        /// <summary>FlagType of the Sekiro JumpTable event (TAE type 0) "End If AI ComboAttack Queued".</summary>
        public const int JumpTableEventType = 0, ComboAttackQueuedFlag = 23;

        /// <summary>
        /// Plays <paramref name="animIds"/> back to back with DSAS's combo player. Every anim except the last is cut at the
        /// start of its "End If AI ComboAttack Queued" window (where the AI's queued ComboRepeat/ComboFinal takes over),
        /// or at its end when it has no such window. Returns what was planned (for display), or null if nothing is loaded.
        /// </summary>
        public List<ComboPlan>? PlayCombo(IReadOnlyList<int> animIds) =>
            PlayCombo(animIds.Select(a => (a, (float?)null)).ToList());

        /// <summary>TAE event types that apply a SpEffect (param s32 at 0x0 = SpEffect id) — CLAUDE.md §4.3.</summary>
        private static readonly int[] SpEffectEventTypes = { 67, 66, 302, 401, 331, 797 };

        /// <summary>SpEffect events in an anim's TAE: (SpEffect id, start frame), sorted by time. Empty if not loaded.</summary>
        public IReadOnlyList<(int SpEffectId, float Frame)> GetSpEffectEvents(int animId)
        {
            try
            {
                var doc = zzz_DocumentManager.CurrentDocument;
                var proj = doc?.EditorScreen?.Proj;
                if (doc == null || proj == null || !doc.EditorScreen.IsFileOpen) return Array.Empty<(int, float)>();
                var taeAnim = proj.SAFE_GetFirstAnimationFromFullID(SplitAnimID.FromFullID(doc.GameRoot, animId));
                if (taeAnim == null) return Array.Empty<(int, float)>();
                float snap = TaeExtensionMethods.GetCurrentSnapInterval();
                return taeAnim.SAFE_GetActions()
                    .Where(a => Array.IndexOf(SpEffectEventTypes, a.Type) >= 0 && a.ParameterBytes != null && a.ParameterBytes.Length >= 4)
                    .Select(a => (BitConverter.ToInt32(a.ParameterBytes, 0), a.StartTime / snap))
                    .OrderBy(x => x.Item2).ToList();
            }
            catch (Exception) { return Array.Empty<(int, float)>(); }
        }

        /// <summary>
        /// Plays anims back to back. <c>CutFrame</c> forces where an anim ends (e.g. a SpEffect interrupt);
        /// otherwise it ends at "End If AI ComboAttack Queued" (or its end for the last anim).
        /// </summary>
        public List<ComboPlan>? PlayCombo(IReadOnlyList<(int AnimId, float? CutFrame)> steps)
        {
            var animIds = steps.Select(s => s.AnimId).ToList();
            var doc = zzz_DocumentManager.CurrentDocument;
            var screen = doc?.EditorScreen;
            var interactor = screen?.Graph?.ViewportInteractor;
            var container = interactor?.CurrentModel?.AnimContainer;
            if (screen == null || !screen.IsFileOpen || interactor == null || container == null || screen.Proj == null) return null;

            float snap = TaeExtensionMethods.GetCurrentSnapInterval();
            var plans = new List<ComboPlan>();
            var entries = new List<NewCombo.Entry>();
            for (int k = 0; k < animIds.Count; k++)
            {
                var id = SplitAnimID.FromFullID(doc!.GameRoot, animIds[k]);
                var taeAnim = screen.Proj.SAFE_GetFirstAnimationFromFullID(id);
                var hkx = taeAnim == null ? null : container.FindAnimation(taeAnim.GetHkxID(screen.Proj), returnNewInstance: false);
                if (taeAnim == null || hkx == null)
                {
                    plans.Add(new ComboPlan(animIds[k], 0, 0, "not in this anibnd", Missing: true));
                    continue;
                }

                float total = Math.Max(1, hkx.FrameCount - 1);
                float end = total;
                string reason = "anim end";
                if (k < animIds.Count - 1)
                {
                    foreach (var act in taeAnim.SAFE_GetActions())
                    {
                        if (act.Type != JumpTableEventType || act.ParameterBytes == null || act.ParameterBytes.Length < 4) continue;
                        if (BitConverter.ToInt32(act.ParameterBytes, 0) != ComboAttackQueuedFlag) continue;
                        float f = act.StartTime / snap;
                        if (f < end) { end = f; reason = "ComboAttack Queued"; }
                    }
                }
                if (steps[k].CutFrame is float forced && forced < total) { end = Math.Max(0, forced); reason = "SpEffect interrupt"; }
                plans.Add(new ComboPlan(animIds[k], end, total, reason, Missing: false));
                entries.Add(new NewCombo.Entry { AnimationID = id, StartFrame = 0, EndFrame = end, BlendDuration = -1, IsLoop = false });
            }

            if (entries.Count == 0) return plans;
            if (screen.PlaybackCursor != null) screen.PlaybackCursor.IsPlaying = false;
            interactor.RequestCombo(entries);
            return plans;
        }

        /// <summary>Pauses/resumes the combo that is playing. Returns false when no combo is active.</summary>
        public bool ToggleComboPause()
        {
            try
            {
                var c = zzz_DocumentManager.CurrentDocument?.EditorScreen?.Graph?.ViewportInteractor?.Combo;
                if (c == null || c.PlaybackState != NewCombo.PlaybackStates.Playing) return false;
                c.Paused = !c.Paused;
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>Play/pause of normal (non-combo) playback, same as DSAS's transport button.</summary>
        public void TogglePlayback() => Safe(() => { zzz_DocumentManager.CurrentDocument?.EditorScreen?.PlaybackCursor?.Transport_PlayPause(); return ""; });

        public bool ComboPaused
        {
            get
            {
                try { return zzz_DocumentManager.CurrentDocument?.EditorScreen?.Graph?.ViewportInteractor?.Combo?.Paused == true; }
                catch (Exception) { return false; }
            }
        }

        public void StopCombo() => Safe(() => { zzz_DocumentManager.CurrentDocument?.EditorScreen?.Graph?.ViewportInteractor?.CancelCombo(); return ""; });

        /// <summary>Index of the combo entry playing now, or -1.</summary>
        public int ComboIndex
        {
            get
            {
                try
                {
                    var c = zzz_DocumentManager.CurrentDocument?.EditorScreen?.Graph?.ViewportInteractor?.Combo;
                    return c != null && c.PlaybackState == NewCombo.PlaybackStates.Playing ? c.CurrentIndex : -1;
                }
                catch (Exception) { return -1; }
            }
        }

        private static T? Safe<T>(Func<T?> f) where T : class
        {
            try { return f(); } catch (Exception) { return null; }
        }
    }
}
