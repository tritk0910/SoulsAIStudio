#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DSAnimStudio;
using DSAnimStudio.TaeEditor;
using SoulsAiStudio.Core.Anim;
using SoulsFormats;
using TAE = SoulsAssetPipeline.Animation.TAE;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// DSAS side of Swap / Add animation (see <see cref="AnimSlots"/>): TAE entries and headers of the loaded anibnd,
    /// the HKX files in its binder (saved with the TAE on Ctrl+S) and the anim container that plays them. Each
    /// operation is one DSAS global undo step (Edit > Undo [Global] Action): the TAE comes back from DSAS's snapshot,
    /// the HKX files and the anim container are put back by our undo callback.
    /// </summary>
    public sealed class AnimSlotsBridge
    {
        private static readonly FieldInfo? BinderField = typeof(TaeFileContainer).GetField("containerBinder", BindingFlags.NonPublic | BindingFlags.Instance);
        private const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

        public static TaeEditorScreen? Tae => zzz_DocumentManager.CurrentDocument?.EditorScreen is { IsFileOpen: true } t ? t : null;

        /// <summary>Why swap/add can't run on the loaded file, or null.</summary>
        public static string? WhyUnavailable(SasApp app)
        {
            var tae = Tae;
            if (tae?.Proj == null) return "Load a character first.";
            if (app.LoadedIsReadOnlyView) return "This character is a read-only view of the game's file. Use \"Copy to mod\" in the Project panel first.";
            if (tae.Proj.ContainerInfo is not DSAProj.TaeContainerInfo.ContainerAnibnd) return "Only a plain .anibnd file is supported.";
            if (Binder(tae) == null) return "Can't access the anibnd's files.";
            return null;
        }

        private static IBinder? Binder(TaeEditorScreen tae) => BinderField?.GetValue(tae.FileContainer) as IBinder;

        // ---------------------------------------------------------------- read

        public sealed record Snapshot(IReadOnlyList<AnimEntryInfo> Entries, IReadOnlySet<int> HkxIds, HashSet<string> CompendiumIds, byte[]? Compendium, int SelectedId);

        public static Snapshot? Read()
        {
            var tae = Tae;
            if (tae?.Proj is not { } proj || Binder(tae) is not { } binder) return null;
            var entries = new List<AnimEntryInfo>();
            foreach (var cat in proj.SAFE_GetAllAnimCategories())
                foreach (var a in cat.SAFE_GetAnimations())
                {
                    if (a.IS_DUMMY_ANIM) continue;
                    var h = a.SAFE_GetHeaderClone();
                    var (kind, refId) = h switch
                    {
                        TAE.Animation.AnimFileHeader.Standard s when s.ImportsHKX && s.ImportHKXSourceAnimID >= 0 => (AnimHeaderKind.OverrideHkx, s.ImportHKXSourceAnimID),
                        TAE.Animation.AnimFileHeader.ImportOtherAnim o => (AnimHeaderKind.ImportOther, o.ImportFromAnimID),
                        _ => (AnimHeaderKind.Own, -1),
                    };
                    entries.Add(new AnimEntryInfo(a.SplitID.GetFullID(proj), kind, refId, h?.AnimFileName ?? ""));
                }
            var hkxFiles = binder.Files.Where(f => f.ID >= AnimSlots.HkxBinderBase).ToList();
            var comp = binder.Files.FirstOrDefault(f => f.ID == 7000000);
            var ids = AnimSlots.CompendiumIds(comp != null ? new[] { comp.Bytes } : Array.Empty<byte[]>(), hkxFiles.Take(40).Select(f => f.Bytes));
            int sel = tae.SelectedAnim is { IS_DUMMY_ANIM: false } s2 ? s2.SplitID.GetFullID(proj) : -1;
            return new Snapshot(entries, hkxFiles.Select(f => f.ID - AnimSlots.HkxBinderBase).ToHashSet(), ids, comp?.Bytes, sel);
        }

        // ---------------------------------------------------------------- swap

        /// <summary>
        /// Applies a swap plan; <paramref name="keepRefs"/> = referencing entries (ids after the swap) to leave alone.
        /// <paramref name="onUndo"/> / <paramref name="onRedo"/> run (next frame, on the UI thread) whenever DSAS undoes /
        /// redoes this global action, so the Lua part of the swap follows (not on the first do: the caller does that).
        /// Returns the handle "Undo last swap" uses.
        /// </summary>
        public static SwapHandle ApplySwap(AnimSwapPlan plan, IReadOnlySet<int> keepRefs, Action? onUndo = null, Action? onRedo = null)
        {
            var tae = Tae ?? throw new InvalidOperationException("No character loaded.");
            var proj = tae.Proj;
            int selected = tae.SelectedAnim?.SplitID.GetFullID(proj) ?? -1;
            bool done = false;
            TaeUndoableAction? action = null;
            tae.GlobalUndoMan.NewAction(
                doAction: () =>
                {
                    if (done && onRedo != null) Main.MainThreadLazyDispatch(onRedo); // redo re-runs this delegate

                    // Entries: find all first (ids change while iterating).
                    var moving = AllAnims(proj).Where(a => plan.EntryMoves.Any(m => m.From == a.SplitID.GetFullID(proj))).ToList();
                    foreach (var a in moving)
                    {
                        int from = a.SplitID.GetFullID(proj), to = plan.Map(from);
                        // DSAS's own id change: re-files the entry in the category's id lookup (setting SplitID alone doesn't).
                        proj.SAFE_RegistAnimationIDChange(a, a.SplitID, SplitAnimID.FromFullID(proj, to));
                        var name = plan.NameChanges.FirstOrDefault(n => n.EntryId == to);
                        if (name.NewName != null)
                        {
                            var h = a.SAFE_GetHeaderClone();
                            h.AnimFileName = name.NewName;
                            a.SAFE_SetHeader(h);
                        }
                        a.SAFE_SetIsModified(true);
                    }
                    foreach (var r in plan.RefChanges.Where(r => !keepRefs.Contains(r.EntryId)))
                        foreach (var a in AllAnims(proj).Where(a => a.SplitID.GetFullID(proj) == r.EntryId))
                        {
                            var h = a.SAFE_GetHeaderClone();
                            if (h is TAE.Animation.AnimFileHeader.Standard s && s.ImportsHKX && s.ImportHKXSourceAnimID == r.OldRef) s.ImportHKXSourceAnimID = r.NewRef;
                            else if (h is TAE.Animation.AnimFileHeader.ImportOtherAnim o && o.ImportFromAnimID == r.OldRef) o.ImportFromAnimID = r.NewRef;
                            else continue;
                            a.SAFE_SetHeader(h);
                            a.SAFE_SetIsModified(true);
                        }
                    foreach (var cat in proj.SAFE_GetAllAnimCategories()) cat.SAFE_ResortAnimIDs();
                    MoveHkx(tae, plan.HkxMoves);
                    AfterChange(tae, plan.Map(selected));
                },
                undoAction: () =>
                {
                    MoveHkx(tae, plan.HkxMoves.Select(m => (m.To, m.From)).ToList()); // DSAS then restores the TAE and reselects
                    if (onUndo != null) Main.MainThreadLazyDispatch(onUndo); // outside DSAS's Invoke + document lock
                },
                actionDescription: $"Swap {AnimSlots.IdName(plan.A)} <-> {AnimSlots.IdName(plan.B)}",
                registAction: a => action = a); // called before the do: gives us the action object (no custom regist)
            done = true;
            return new SwapHandle(tae, action!, AnimUndoTops(tae));
        }

        // ---------------------------------------------------------------- undo of one exact global action

        /// <summary>A global undo action of ours, plus the top of every per-animation undo stack right after it.</summary>
        public sealed class SwapHandle
        {
            internal SwapHandle(TaeEditorScreen tae, TaeUndoableAction action, Dictionary<TaeUndoMan, TaeUndoableAction?> animTops)
            {
                Tae = tae; Action = action; AnimTops = animTops;
            }
            internal TaeEditorScreen Tae { get; }
            internal TaeUndoableAction Action { get; }
            internal Dictionary<TaeUndoMan, TaeUndoableAction?> AnimTops { get; }
        }

        private static readonly FieldInfo? UndoStackField = typeof(TaeUndoMan).GetField("UndoStack", Priv);

        private static TaeUndoableAction? Top(TaeUndoMan? man) =>
            man != null && UndoStackField?.GetValue(man) is Stack<TaeUndoableAction> { Count: > 0 } st ? st.Peek() : null;

        private static Dictionary<TaeUndoMan, TaeUndoableAction?> AnimUndoTops(TaeEditorScreen tae)
        {
            var result = new Dictionary<TaeUndoMan, TaeUndoableAction?>();
            foreach (var man in tae.UndoManDictionary.Values.ToList()) result[man] = Top(man);
            return result;
        }

        /// <summary>
        /// Why <paramref name="h"/> can't be undone by itself now, or null. Undoing a DSAS global action restores the whole
        /// file as it was before it, so it is refused when anything was done to the file after it: a newer global action on
        /// top of it, or per-animation edits (their undo stacks moved since).
        /// </summary>
        public static string? WhyCantUndo(SwapHandle h)
        {
            if (UndoStackField == null) return "This DSAS build has no accessible undo stack.";
            if (!ReferenceEquals(Tae, h.Tae) || h.Tae.Proj == null) return "The character it was made on is no longer the loaded one.";
            var top = Top(h.Tae.GlobalUndoMan);
            if (!ReferenceEquals(top, h.Action))
            {
                bool below = UndoStackField.GetValue(h.Tae.GlobalUndoMan) is Stack<TaeUndoableAction> st && st.Contains(h.Action);
                return below
                    ? $"Newer [Global] actions were made after it (last: \"{top?.ActionDescription}\"). Undo those first with Edit > Undo [Global] Action (Ctrl+Shift+Z)."
                    : "It is no longer in the [Global] undo history (already undone, or the file was reloaded).";
            }
            int edited = 0;
            foreach (var man in h.Tae.UndoManDictionary.Values.ToList())
                if (!ReferenceEquals(Top(man), h.AnimTops.GetValueOrDefault(man))) edited++;
            if (edited > 0)
                return $"{edited} animation{(edited == 1 ? " was" : "s were")} edited after the swap; undoing it restores the whole file and would lose " +
                       "those edits. Undo them first (Ctrl+Z), or use Edit > Undo [Global] Action (Ctrl+Shift+Z) to accept losing them.";
            return null;
        }

        /// <summary>Undoes <paramref name="h"/> through DSAS's global undo (its undo callback then reverts the Lua part).</summary>
        public static void Undo(SwapHandle h)
        {
            if (WhyCantUndo(h) is { } why) throw new InvalidOperationException(why);
            h.Tae.GlobalUndoMan.Undo();
        }

        // ---------------------------------------------------------------- add

        /// <summary>
        /// Adds an entry at <paramref name="id"/> with no events: playing the HKX of <paramref name="overrideFrom"/>
        /// (Override HKX), or its own HKX <paramref name="hkxBytes"/> (added to the anibnd).
        /// </summary>
        public static void ApplyAdd(int id, string label, int? overrideFrom, byte[]? hkxBytes)
        {
            var tae = Tae ?? throw new InvalidOperationException("No character loaded.");
            var proj = tae.Proj;
            var split = SplitAnimID.FromFullID(proj, id);
            int selected = tae.SelectedAnim?.SplitID.GetFullID(proj) ?? -1;
            tae.GlobalUndoMan.NewAction(
                doAction: () =>
                {
                    var cat = proj.SAFE_RegistCategory(split.CategoryID);
                    var anim = new DSAProj.Animation(proj, cat);
                    anim.SAFE_SetHeader(new TAE.Animation.AnimFileHeader.Standard
                    {
                        AnimFileName = label,
                        ImportsHKX = overrideFrom != null,
                        ImportHKXSourceAnimID = overrideFrom ?? -1,
                        IsNullHeader = false,
                    });
                    anim.SplitID = split;
                    cat.SAFE_AddAnimation(anim);
                    cat.SAFE_ResortAnimIDs();
                    anim.SAFE_SetIsModified(true);
                    if (hkxBytes != null) AddHkx(tae, id, hkxBytes);
                    AfterChange(tae, id);
                },
                undoAction: () => { if (hkxBytes != null) RemoveHkx(tae, id); },
                actionDescription: $"Add {AnimSlots.IdName(id)}");
        }

        // ---------------------------------------------------------------- replace

        /// <summary>The HKX file bytes the anibnd holds for <paramref name="id"/>, or null when it has none of its own.</summary>
        public static byte[]? CurrentHkx(int id) =>
            Tae is { } t && Binder(t)?.Files.FirstOrDefault(f => f.ID == AnimSlots.HkxBinderId(id)) is { } f ? f.Bytes : null;

        /// <summary>
        /// Replaces the HKX of <paramref name="id"/> with <paramref name="bytes"/> (one DSAS global undo step; saved into the
        /// anibnd with Ctrl+S, which backs the anibnd up first). The TAE entry and its events are left alone.
        /// </summary>
        public static void ApplyReplace(int id, byte[] bytes)
        {
            var tae = Tae ?? throw new InvalidOperationException("No character loaded.");
            var binder = Binder(tae) ?? throw new InvalidOperationException("Can't access the anibnd's files.");
            var file = binder.Files.FirstOrDefault(f => f.ID == AnimSlots.HkxBinderId(id))
                       ?? throw new InvalidOperationException($"{AnimSlots.IdName(id)} has no HKX file of its own in this anibnd.");
            byte[] old = file.Bytes;
            tae.GlobalUndoMan.NewAction(
                doAction: () => { SetHkx(tae, id, bytes); AfterChange(tae, id); },
                undoAction: () => SetHkx(tae, id, old),
                actionDescription: $"Replace HKX of {AnimSlots.IdName(id)}");
        }

        private static void SetHkx(TaeEditorScreen tae, int id, byte[] bytes)
        {
            if (Binder(tae) is not { } binder) return;
            var file = binder.Files.FirstOrDefault(f => f.ID == AnimSlots.HkxBinderId(id));
            if (file == null) return;
            file.Bytes = bytes;
            string? compName = binder.Files.FirstOrDefault(f => f.ID == 7000000)?.Name;
            var container = tae.Graph?.ViewportInteractor?.CurrentModel?.AnimContainer;
            if (container != null)
            {
                RemoveFromContainer(tae, id); // AddNewHKXToLoad throws on an existing id
                container.AddNewHKXToLoad(SplitAnimID.FromFullID(tae.Proj, id), System.IO.Path.GetFileName(file.Name), bytes, compName);
            }
        }

        // ---------------------------------------------------------------- HKX files + anim container

        private static IEnumerable<DSAProj.Animation> AllAnims(DSAProj proj) =>
            proj.SAFE_GetAllAnimCategories().SelectMany(c => c.SAFE_GetAnimations()).Where(a => !a.IS_DUMMY_ANIM).ToList();

        private static string HkxName(IBinder binder, int id)
        {
            var sample = binder.Files.FirstOrDefault(f => f.ID >= AnimSlots.HkxBinderBase)?.Name
                         ?? binder.Files.FirstOrDefault(f => f.ID == 1000000)?.Name ?? "";
            int slash = sample.LastIndexOf('\\');
            return (slash >= 0 ? sample[..(slash + 1)] : "") + AnimSlots.IdName(id) + ".hkx";
        }

        private static string Rename(string name, int from, int to)
        {
            int slash = name.LastIndexOf('\\');
            string file = name[(slash + 1)..];
            return name[..(slash + 1)] + (AnimSlots.RenameAutoName(file, from, to) ?? AnimSlots.IdName(to) + ".hkx");
        }

        /// <summary>Moves HKX files (all at once, so a↔b works) in the binder and in the anim container.</summary>
        private static void MoveHkx(TaeEditorScreen tae, IReadOnlyList<(int From, int To)> moves)
        {
            if (moves.Count == 0 || Binder(tae) is not { } binder) return;
            var files = moves.Select(m => (m, f: binder.Files.FirstOrDefault(f => f.ID == AnimSlots.HkxBinderId(m.From)))).ToList();
            foreach (var (m, f) in files)
                if (f != null) { f.ID = AnimSlots.HkxBinderId(m.To); f.Name = Rename(f.Name, m.From, m.To); }
            binder.Files = binder.Files.OrderBy(f => f.ID).ToList();

            WithContainer(tae, c =>
            {
                var proj = tae.Proj;
                foreach (var dictName in new[] { "animHKXsToLoad", "animHKXsNameAssign", "animHKXsCompendiumAssign" })
                {
                    if (c.Field(dictName) is not IDictionary dict) continue;
                    var taken = moves.Select(m => (m, key: (object)SplitAnimID.FromFullID(proj, m.From)))
                        .Select(x => (x.m, has: dict.Contains(x.key), val: dict.Contains(x.key) ? dict[x.key] : null)).ToList();
                    foreach (var x in taken) dict.Remove(SplitAnimID.FromFullID(proj, x.m.From));
                    foreach (var x in taken)
                        if (x.has)
                        {
                            object? v = x.val;
                            if (dictName == "animHKXsNameAssign" && v is string s) v = AnimSlots.RenameAutoName(s, x.m.From, x.m.To) ?? s;
                            dict[SplitAnimID.FromFullID(proj, x.m.To)] = v;
                        }
                }
                if (c.Field("AnimationCache") is IDictionary cache)
                    foreach (var m in moves) { cache.Remove(SplitAnimID.FromFullID(proj, m.From)); cache.Remove(SplitAnimID.FromFullID(proj, m.To)); }
            });
        }

        private static void AddHkx(TaeEditorScreen tae, int id, byte[] bytes)
        {
            if (Binder(tae) is not { } binder) return;
            var sample = binder.Files.FirstOrDefault(f => f.ID >= AnimSlots.HkxBinderBase);
            string name = HkxName(binder, id);
            binder.Files.RemoveAll(f => f.ID == AnimSlots.HkxBinderId(id));
            binder.Files.Add(new BinderFile(sample?.Flags ?? SoulsFormats.Binder.FileFlags.Flag1, AnimSlots.HkxBinderId(id), name, bytes));
            binder.Files = binder.Files.OrderBy(f => f.ID).ToList();
            string? compName = binder.Files.FirstOrDefault(f => f.ID == 7000000)?.Name;
            var container = tae.Graph?.ViewportInteractor?.CurrentModel?.AnimContainer;
            if (container != null)
            {
                RemoveFromContainer(tae, id); // AddNewHKXToLoad throws on an existing id
                container.AddNewHKXToLoad(SplitAnimID.FromFullID(tae.Proj, id), System.IO.Path.GetFileName(name), bytes, compName);
            }
        }

        private static void RemoveHkx(TaeEditorScreen tae, int id)
        {
            if (Binder(tae) is { } binder) binder.Files.RemoveAll(f => f.ID == AnimSlots.HkxBinderId(id));
            RemoveFromContainer(tae, id);
        }

        private static void RemoveFromContainer(TaeEditorScreen tae, int id) => WithContainer(tae, c =>
        {
            var key = SplitAnimID.FromFullID(tae.Proj, id);
            foreach (var n in new[] { "animHKXsToLoad", "animHKXsNameAssign", "animHKXsCompendiumAssign", "AnimationCache" })
                if (c.Field(n) is IDictionary d) d.Remove(key);
        });

        private sealed class ContainerAccess(NewAnimationContainer c)
        {
            public object? Field(string name) => typeof(NewAnimationContainer).GetField(name, Priv)?.GetValue(c);
        }

        private static void WithContainer(TaeEditorScreen tae, Action<ContainerAccess> act)
        {
            var c = tae.Graph?.ViewportInteractor?.CurrentModel?.AnimContainer;
            if (c == null) return;
            object lockObj = typeof(NewAnimationContainer).GetField("_lock_AnimCacheAndDict", Priv)?.GetValue(c) ?? c;
            lock (lockObj) act(new ContainerAccess(c));
        }

        /// <summary>Refresh the anim list and select <paramref name="id"/> (which replays it from the new data).</summary>
        private static void AfterChange(TaeEditorScreen tae, int id)
        {
            tae.RecreateAnimList();
            if (id >= 0) tae.GotoAnimID(SplitAnimID.FromFullID(tae.Proj, id), true, false, out _);
        }

        // ---------------------------------------------------------------- HKX skeleton check

        /// <summary>
        /// Loads <paramref name="hkx"/> with the character's compendium and compares it with the loaded skeleton and with
        /// an existing anim of the file. Returns problems (empty = looks fine).
        /// </summary>
        public static List<string> CheckAgainstSkeleton(byte[] hkx, byte[]? compendium)
        {
            var problems = new List<string>();
            var tae = Tae;
            var container = tae?.Graph?.ViewportInteractor?.CurrentModel?.AnimContainer;
            if (container == null) { problems.Add("The character's model isn't loaded, so the skeleton could not be checked."); return problems; }
            SoulsAssetPipeline.Animation.HKX? parsed;
            try { parsed = container.GetHkxStructOfAnim(hkx, compendium); }
            catch (Exception ex) { problems.Add("Could not read the file as an animation: " + ex.Message); return problems; }
            if (parsed == null) { problems.Add("Could not read the file as an animation."); return problems; }
            var objs = parsed.DataSection.Objects;
            bool hasAnim = objs.Any(o => o is SoulsAssetPipeline.Animation.HKX.HKASplineCompressedAnimation or SoulsAssetPipeline.Animation.HKX.HKAInterleavedUncompressedAnimation);
            var binding = objs.OfType<SoulsAssetPipeline.Animation.HKX.HKAAnimationBinding>().FirstOrDefault();
            if (!hasAnim) problems.Add("The file has no animation data (spline or interleaved).");
            if (binding == null) { problems.Add("The file has no animation binding (track → bone map)."); return problems; }
            int bones = container.Skeleton?.Bones?.Count ?? 0;
            var map = binding.TransformTrackToBoneIndices?.GetArrayData()?.Elements?.Select(e => (int)e.data).ToList() ?? new List<int>();
            if (bones > 0 && map.Count > 0 && map.Max() >= bones)
                problems.Add($"It animates bone #{map.Max()}, but this character's skeleton has {bones} bones: it was made for another skeleton.");
            // Compare with an existing anim of this anibnd (same skeleton name expected).
            string? refName = ReferenceSkeletonName(container, compendium);
            if (refName != null && binding.OriginalSkeletonName != null && !string.Equals(refName, binding.OriginalSkeletonName, StringComparison.OrdinalIgnoreCase))
                problems.Add($"It was bound to skeleton \"{binding.OriginalSkeletonName}\", this character's anims use \"{refName}\". Retarget it to this skeleton first.");
            return problems;
        }

        private static string? ReferenceSkeletonName(NewAnimationContainer c, byte[]? compendium)
        {
            try
            {
                foreach (var bytes in c.Animations.Values.Take(3))
                {
                    var h = c.GetHkxStructOfAnim(bytes, compendium);
                    var b = h?.DataSection.Objects.OfType<SoulsAssetPipeline.Animation.HKX.HKAAnimationBinding>().FirstOrDefault();
                    if (!string.IsNullOrEmpty(b?.OriginalSkeletonName)) return b!.OriginalSkeletonName;
                }
            }
            catch (Exception) { }
            return null;
        }
    }
}
