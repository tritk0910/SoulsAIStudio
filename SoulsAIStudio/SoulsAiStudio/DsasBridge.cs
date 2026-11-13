#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using DSAnimStudio;
using DSAnimStudio.TaeEditor;
using SoulsAiStudio.Core.Combo;
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

        /// <summary>One combo step as planned: frames StartFrame–EndFrame of TotalFrames (TAE frames), and why it ends there.</summary>
        /// <param name="Warn">The requested cut was not found in the TAE, so the anim plays to its end.</param>
        public sealed record ComboPlan(int AnimId, float EndFrame, float TotalFrames, string CutReason, bool Missing, float StartFrame = 0, bool Warn = false);

        /// <summary>One anim to play and how it ends. ForcedCut/EndFrame (TAE frames) win over Cut.</summary>
        public sealed record ComboRequest(int AnimId, ComboCut Cut, float? ForcedCut = null, string? ForcedReason = null,
            float? StartFrame = null, float? EndFrame = null);

        /// <summary>FlagType of the Sekiro JumpTable event (TAE type 0) "End If AI ComboAttack Queued".</summary>
        public const int JumpTableEventType = 0, ComboAttackQueuedFlag = 23;

        /// <summary>TAE event types that apply a SpEffect (param s32 at 0x0 = SpEffect id) — CLAUDE.md §4.3.</summary>
        private static readonly int[] SpEffectEventTypes = { 67, 66, 302, 401, 331, 797 };

        /// <summary>SpEffect events in an anim's TAE: (SpEffect id, start frame, end frame), sorted by time. Empty if not loaded.</summary>
        public IReadOnlyList<(int SpEffectId, float Frame, float EndFrame)> GetSpEffectEvents(int animId) =>
            Events(animId, a => Array.IndexOf(SpEffectEventTypes, a.Type) >= 0);

        /// <summary>JumpTable flags in an anim's TAE: (flag, start frame), sorted by time. Empty if not loaded.</summary>
        public IReadOnlyList<(int Flag, float Frame)> GetJumpTableEvents(int animId) =>
            Events(animId, a => a.Type == JumpTableEventType).Select(e => (e.Value, e.Frame)).ToList();

        /// <summary>Start frame of the anim's "End If AI ComboAttack Queued" window (where a queued next anim takes over), or null.</summary>
        public float? GetComboQueuedFrame(int animId)
        {
            foreach (var (flag, frame) in GetJumpTableEvents(animId))
                if (flag == ComboAttackQueuedFlag) return frame;
            return null;
        }

        /// <summary>(s32 at 0x0, start frame, end frame) of the matching events of an anim, sorted by time.</summary>
        private static IReadOnlyList<(int Value, float Frame, float EndFrame)> Events(int animId, Func<DSAProj.Action, bool> match)
        {
            try
            {
                var doc = zzz_DocumentManager.CurrentDocument;
                var proj = doc?.EditorScreen?.Proj;
                if (doc == null || proj == null || !doc.EditorScreen.IsFileOpen) return Array.Empty<(int, float, float)>();
                var taeAnim = proj.SAFE_GetFirstAnimationFromFullID(SplitAnimID.FromFullID(doc.GameRoot, animId));
                if (taeAnim == null) return Array.Empty<(int, float, float)>();
                float snap = TaeExtensionMethods.GetCurrentSnapInterval();
                return taeAnim.SAFE_GetActions()
                    .Where(a => match(a) && a.ParameterBytes != null && a.ParameterBytes.Length >= 4)
                    .Select(a => (BitConverter.ToInt32(a.ParameterBytes, 0), a.StartTime / snap, a.EndTime / snap))
                    .OrderBy(x => x.Item2).ToList();
            }
            catch (Exception) { return Array.Empty<(int, float, float)>(); }
        }

        /// <summary>
        /// Lua combo path: every anim except the last is cut at "End If AI ComboAttack Queued" (or its end), unless
        /// <c>CutFrame</c> forces a SpEffect interrupt cut.
        /// </summary>
        public List<ComboPlan>? PlayCombo(IReadOnlyList<(int AnimId, float? CutFrame)> steps) =>
            PlayCombo(steps.Select((s, i) => new ComboRequest(s.AnimId,
                i < steps.Count - 1 ? ComboCut.ComboAttackQueued : ComboCut.NoCancel,
                s.CutFrame, s.CutFrame != null ? "SpEffect interrupt" : null)).ToList());

        /// <summary>Resolves (and with <paramref name="play"/> plays) a combo. Null when no character is loaded.</summary>
        public List<ComboPlan>? PlayCombo(IReadOnlyList<ComboRequest> steps, bool play = true)
        {
            var doc = zzz_DocumentManager.CurrentDocument;
            var screen = doc?.EditorScreen;
            var interactor = screen?.Graph?.ViewportInteractor;
            var container = interactor?.CurrentModel?.AnimContainer;
            if (screen == null || !screen.IsFileOpen || interactor == null || container == null || screen.Proj == null) return null;

            float snap = TaeExtensionMethods.GetCurrentSnapInterval();
            var plans = new List<ComboPlan>();
            var entries = new List<NewCombo.Entry>();
            foreach (var req in steps)
            {
                var id = SplitAnimID.FromFullID(doc!.GameRoot, req.AnimId);
                var taeAnim = screen.Proj.SAFE_GetFirstAnimationFromFullID(id);
                var hkx = taeAnim == null ? null : container.FindAnimation(taeAnim.GetHkxID(screen.Proj), returnNewInstance: false);
                if (taeAnim == null || hkx == null)
                {
                    plans.Add(new ComboPlan(req.AnimId, 0, 0, "not in this anibnd", Missing: true));
                    continue;
                }

                // In TAE frames (snap = 1/30 s for Sekiro). Not hkx.FrameCount: some HKX are 60 fps (e.g. Owl 3048 = 213 frames,
                // 3.53 s), which made the combo hold/loop the last anim for twice its length.
                float total = Math.Max(1, (float)Math.Round(hkx.Duration / snap));
                float start = Math.Clamp(req.StartFrame ?? 0, 0, total);
                float end = total;
                string reason = "anim end";
                bool warn = false;
                if (req.EndFrame is float ef) { end = ef; reason = "end frame (script)"; }
                else if (req.ForcedCut is float fc) { end = fc; reason = req.ForcedReason ?? "forced cut"; }
                else if (req.Cut.SpEffectId is int sp)
                {
                    var w = FirstWindow(taeAnim, snap, a => Array.IndexOf(SpEffectEventTypes, a.Type) >= 0, sp);
                    if (w is not { } win) { reason = $"SpEffect {sp} not in TAE → anim end"; warn = true; }
                    else if (req.Cut.AtFrame is float at)
                    {
                        end = Math.Clamp(at, win.Start, win.End);
                        reason = $"SpEffect {sp} @ f{end:0} (active f{win.Start:0}–{win.End:0})";
                        if (at < win.Start || at > win.End) { reason += $": f{at:0} is outside, clamped"; warn = true; }
                    }
                    else { end = win.Start; reason = $"SpEffect {sp}"; }
                }
                else if (req.Cut.JumpTableFlag >= 0)
                {
                    float? f = FirstFrame(taeAnim, snap, a => a.Type == JumpTableEventType, req.Cut.JumpTableFlag);
                    string name = req.Cut.JumpTableFlag == ComboAttackQueuedFlag ? "ComboAttack Queued" : $"JumpTable {req.Cut.JumpTableFlag}";
                    if (f is float jf) { end = jf; reason = name; }
                    else { reason = $"no {name} → anim end"; warn = true; }
                }
                end = Math.Clamp(end, start, total);
                plans.Add(new ComboPlan(req.AnimId, end, total, reason, Missing: false, start, warn));
                entries.Add(new NewCombo.Entry { AnimationID = id, StartFrame = start, EndFrame = end, BlendDuration = -1, IsLoop = false });
            }

            if (!play || entries.Count == 0) return plans;
            if (screen.PlaybackCursor != null) screen.PlaybackCursor.IsPlaying = false;
            interactor.RequestCombo(entries);
            return plans;
        }

        /// <summary>Start/end frame of the earliest matching event.</summary>
        private static (float Start, float End)? FirstWindow(DSAProj.Animation taeAnim, float snap, Func<DSAProj.Action, bool> match, int value)
        {
            (float Start, float End)? best = null;
            foreach (var act in taeAnim.SAFE_GetActions())
                if (match(act) && act.ParameterBytes is { Length: >= 4 } pb && BitConverter.ToInt32(pb, 0) == value
                    && (best == null || act.StartTime / snap < best.Value.Start))
                    best = (act.StartTime / snap, act.EndTime / snap);
            return best;
        }

        private static float? FirstFrame(DSAProj.Animation taeAnim, float snap, Func<DSAProj.Action, bool> match, int value)
        {
            float? best = null;
            foreach (var act in taeAnim.SAFE_GetActions())
                if (match(act) && act.ParameterBytes is { Length: >= 4 } pb && BitConverter.ToInt32(pb, 0) == value
                    && (best == null || act.StartTime / snap < best))
                    best = act.StartTime / snap;
            return best;
        }

        /// <summary>True once a combo has played to its last entry's end (not when stopped or never started).</summary>
        public bool ComboFinished
        {
            get
            {
                try { return zzz_DocumentManager.CurrentDocument?.EditorScreen?.Graph?.ViewportInteractor?.Combo?.PlaybackState == NewCombo.PlaybackStates.Finished; }
                catch (Exception) { return false; }
            }
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

        public void SetRootMotionBlending(bool on) => Safe(() =>
        {
            var c = zzz_DocumentManager.CurrentDocument?.EditorScreen?.Graph?.ViewportInteractor?.CurrentModel?.AnimContainer;
            if (c != null) c.EnableRootMotionBlending = on;
            return "";
        });

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
