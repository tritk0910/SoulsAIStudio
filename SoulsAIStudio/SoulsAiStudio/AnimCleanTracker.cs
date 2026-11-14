#nullable enable
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using DSAnimStudio;
using DSAnimStudio.TaeEditor;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// Clears an animation's unsaved mark (<c>*</c> in the Animations list) when Undo/Redo brings it back to its saved
    /// state. DSAS marks the anim modified on every undo/redo, so moving a block and pressing Ctrl+Z left the <c>*</c>.
    /// <para>
    /// The anim's serialized bytes are remembered while it is clean (right before the first edit / undo / redo after a
    /// load or save). After an undo/redo the anim is serialized again; equal bytes = saved state → not modified. Edits that
    /// bypass the undo system (no baseline, or bytes differ) keep the mark, so this can only remove a mark that is wrong.
    /// </para>
    /// Called from three <c>// [SAS]</c> lines in <c>TaeUndoMan</c> and one in <c>DSAProj.INNER_ClearAllModified</c>.
    /// </summary>
    public sealed class AnimCleanTracker
    {
        private sealed class Baseline
        {
            public required byte[] Bytes;
            /// <summary>The anim's category had no other unsaved change when the baseline was taken.</summary>
            public bool CategoryWasClean;
        }

        private readonly ConditionalWeakTable<DSAProj.Animation, Baseline> _clean = new();

        /// <summary>Before an edit or an undo/redo of <paramref name="anim"/>: remember its state if it is unsaved-free.</summary>
        public void BeforeChange(DSAProj.Animation? anim)
        {
            if (anim == null || anim.IS_DUMMY_ANIM || anim.ParentProj == null) return;
            if (anim.SAFE_GetIsModified()) return; // keep the baseline from before the first change
            _clean.AddOrUpdate(anim, new Baseline
            {
                Bytes = anim.SerializeToBytes(anim.ParentProj),
                CategoryWasClean = anim.ParentCategory == null || !anim.ParentCategory.SAFE_GetIsModified(),
            });
        }

        /// <summary>After an undo/redo (DSAS has just marked the anim modified): unmark it if it equals the saved state.</summary>
        public void AfterUndoRedo(DSAProj.Animation? anim)
        {
            if (anim == null || anim.ParentProj == null || !_clean.TryGetValue(anim, out var baseline)) return;
            byte[] now = anim.SerializeToBytes(anim.ParentProj);
            if (!now.AsSpan().SequenceEqual(baseline.Bytes)) return;

            anim.SAFE_SetIsModified(false);
            var category = anim.ParentCategory;
            if (category == null || !baseline.CategoryWasClean) return;
            bool othersModified = false;
            category.SafeAccessAnimations(list => othersModified = list.Any(a => a.INNER_GetIsModified()));
            // Category flag only came from its anims → clean again; this also clears the project's (title bar) mark.
            if (!othersModified) category.SAFE_SetIsModified(false);
        }

        /// <summary>DSAS saved (all modified flags cleared): baselines are outdated.</summary>
        public void Reset() => _clean.Clear();
    }
}
