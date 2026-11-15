#nullable enable
using System;
using System.Collections.Generic;
using DSAnimStudio;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// Animations list (DSAS 4.9.9 behaviour): every anim that plays the same HKX as the selected anim, through its own
    /// file, an "import HKX" override or an imported anim, is highlighted. Computed once per selection (and refreshed
    /// every 2 s for header edits), not per row per frame.
    /// </summary>
    public sealed class SameHkxIndex
    {
        private DSAProj? _proj;
        private DSAProj.Animation? _selected;
        private DateTime _builtAt;
        private readonly HashSet<DSAProj.Animation> _same = new();

        public bool Shares(DSAProj? proj, DSAProj.Animation? selected, DSAProj.Animation anim)
        {
            if (proj == null || selected == null || anim == null || ReferenceEquals(anim, selected)) return false;
            if (!ReferenceEquals(proj, _proj) || !ReferenceEquals(selected, _selected) || (DateTime.UtcNow - _builtAt).TotalSeconds > 2)
                Rebuild(proj, selected);
            return _same.Contains(anim);
        }

        private void Rebuild(DSAProj proj, DSAProj.Animation selected)
        {
            _proj = proj;
            _selected = selected;
            _builtAt = DateTime.UtcNow;
            _same.Clear();
            if (selected.IS_DUMMY_ANIM) return;
            var hkx = selected.GetHkxID(proj);
            if (!hkx.IsValid) return;
            foreach (var category in proj.SAFE_GetAllAnimCategories())
                category.SafeAccessAnimations(anims =>
                {
                    foreach (var a in anims)
                        if (!a.IS_DUMMY_ANIM && !ReferenceEquals(a, selected) && a.GetHkxID(proj) == hkx)
                            _same.Add(a);
                });
        }
    }
}
