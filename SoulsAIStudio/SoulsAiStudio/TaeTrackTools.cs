#nullable enable
using System.Collections.Generic;
using System.Linq;
using DSAnimStudio;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// Rows (action tracks) of a TAE animation in the graph. DSAS adds rows for pasted actions without a name (drawn
    /// "%null% (Row N)" until the file is reopened) and keeps rows that hold nothing.
    /// </summary>
    public static class TaeTrackTools
    {
        /// <summary>Removes rows without any action (actions keep their row, renumbered). Returns how many were removed.</summary>
        public static int RemoveEmptyTracks(DSAProj.Animation anim)
        {
            int removed = 0;
            anim.SafeAccessActionTracks(tracks =>
            {
                anim.SafeAccessActions(actions =>
                {
                    var used = new HashSet<int>(actions.Select(a => a.TrackIndex));
                    var map = new int[tracks.Count];
                    var kept = new List<DSAProj.ActionTrack>(tracks.Count);
                    for (int i = 0; i < tracks.Count; i++)
                    {
                        if (used.Contains(i)) { map[i] = kept.Count; kept.Add(tracks[i]); }
                        else { map[i] = -1; removed++; }
                    }
                    if (removed == 0) return;
                    foreach (var a in actions)
                        if (a.TrackIndex >= 0 && a.TrackIndex < map.Length) a.TrackIndex = map[a.TrackIndex];
                    tracks.Clear();
                    tracks.AddRange(kept);
                });
            });
            return removed;
        }

        /// <summary>Names rows that have no name yet from their first action (as DSAS does when it opens the file).</summary>
        public static void NameUnnamedTracks(DSAProj.Animation anim, DSAProj? proj)
        {
            if (proj?.Template != null) anim.SAFE_GenerateTrackNames(proj.Template, overwriteExisting: false);
        }
    }
}
