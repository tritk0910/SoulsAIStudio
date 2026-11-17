#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using DSAnimStudio;
using DSAnimStudio.TaeEditor;
using SoulsAiStudio.Core.Combo;
using SoulsAiStudio.Core.Formats.Params;
using SoulsFormats;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// OnHit / behavior SpEffect sources (CLAUDE.md §P2): TAE AttackBehavior / Bullet / CommonBehavior → BehaviorParam →
    /// AtkParam_Npc / BulletParam SpEffect fields. DSAS's own AtkParam reader skips <c>spEffectId0..4</c> and DSAS does not
    /// load BulletParam, so those fields are read from the raw rows of DSAS's already loaded params at the Paramdex
    /// offsets of <see cref="SekiroParamOffsets"/> (verified against ApplyParamdef in ParamLayoutTests). Sekiro only.
    /// </summary>
    public sealed partial class DsasBridge
    {
        private DsasBehaviorParams? _behaviorParams;

        /// <summary>
        /// SpEffects applied by the behavior events of an anim (on hit, or on self when the event runs), sorted by frame.
        /// Null when nothing can be resolved yet (no character / params loaded, anim not found): callers should not cache that.
        /// </summary>
        public IReadOnlyList<BehaviorSpEffect>? GetBehaviorSpEffects(int animId)
        {
            try
            {
                var doc = zzz_DocumentManager.CurrentDocument;
                var screen = doc?.EditorScreen;
                var proj = screen?.Proj;
                var pm = doc?.ParamManager;
                if (doc == null || screen == null || proj == null || !screen.IsFileOpen || pm == null) return null;
                if (doc.GameRoot.GameType != SoulsAssetPipeline.SoulsGames.SDT) return Array.Empty<BehaviorSpEffect>();
                if (pm.BehaviorParam == null || pm.BehaviorParam.IsEmpty || pm.AtkParam_Npc == null || pm.AtkParam_Npc.IsEmpty) return null;
                var taeAnim = proj.SAFE_GetFirstAnimationFromFullID(SplitAnimID.FromFullID(doc.GameRoot, animId));
                if (taeAnim == null) return null;
                var npc = screen.Graph?.ViewportInteractor?.CurrentModel?.NpcParam;

                if (_behaviorParams == null || !_behaviorParams.IsFor(pm)) _behaviorParams = new DsasBehaviorParams(pm);
                float snap = TaeExtensionMethods.GetCurrentSnapInterval();
                var events = taeAnim.SAFE_GetActions()
                    .Where(a => SekiroBehaviorEvents.JudgeIdOffset(a.Type) >= 0 && a.ParameterBytes != null)
                    .Select(a => new TaeEventData(a.Type, a.StartTime / snap, a.EndTime / snap, a.ParameterBytes))
                    .ToList();
                return BehaviorSpEffects.Resolve(events, npc?.BehaviorVariationID, _behaviorParams);
            }
            catch (Exception) { return null; }
        }

        /// <summary>Param lookups over DSAS's loaded params (row index built once per param instance).</summary>
        private sealed class DsasBehaviorParams : IBehaviorParamSource
        {
            private readonly zzz_ParamManagerIns _pm;
            private readonly Dictionary<int, int[]?> _atk = new();
            private readonly Dictionary<int, BulletRow?> _bullet = new();
            private Dictionary<int, PARAM_Hack.Row>? _atkRows, _bulletRows;
            private PARAM_Hack? _atkParam, _bulletParam;
            private bool _bulletTried;

            public DsasBehaviorParams(zzz_ParamManagerIns pm) => _pm = pm;

            /// <summary>Still the same loaded params (a param reload creates new PARAM instances).</summary>
            public bool IsFor(zzz_ParamManagerIns pm)
            {
                if (!ReferenceEquals(pm, _pm)) return false;
                if (_atkParam == null) return true;
                try { return ReferenceEquals(pm.GetParam("AtkParam_Npc"), _atkParam); }
                catch (Exception) { return false; }
            }

            public BehaviorRow? Behavior(long id) =>
                _pm.BehaviorParam.TryGetValue(id, out var b) ? new BehaviorRow((int)b.RefType, b.RefID) : null;

            public IReadOnlyList<int>? AtkSpEffects(int atkId)
            {
                if (_atk.TryGetValue(atkId, out var cached)) return cached;
                if (_atkRows == null)
                {
                    _atkParam = _pm.GetParam("AtkParam_Npc");
                    _atkRows = Index(_atkParam);
                }
                int[]? result = null;
                if (_atkParam != null && _atkRows.TryGetValue(atkId, out var row))
                    result = ReadIds(_atkParam, row, SekiroParamOffsets.AtkSpEffectId0, SekiroParamOffsets.AtkRowSize);
                _atk[atkId] = result;
                return result;
            }

            public BulletRow? Bullet(int id)
            {
                if (_bullet.TryGetValue(id, out var cached)) return cached;
                if (!_bulletTried)
                {
                    _bulletTried = true;
                    _bulletParam = _pm.GetParam("Bullet");
                    _bulletRows = Index(_bulletParam);
                }
                BulletRow? result = null;
                if (_bulletParam != null && _bulletRows != null && _bulletRows.TryGetValue(id, out var row) && Fits(_bulletParam, SekiroParamOffsets.BulletRowSize))
                {
                    var br = _bulletParam.GetRowReader(row);
                    long start = br.Position;
                    result = new BulletRow(br.GetInt32(start + SekiroParamOffsets.BulletAtkId),
                        ReadIds(_bulletParam, row, SekiroParamOffsets.BulletSpEffectId0, SekiroParamOffsets.BulletRowSize) ?? Array.Empty<int>(),
                        br.GetInt32(start + SekiroParamOffsets.BulletSpEffectIdForShooter));
                }
                _bullet[id] = result;
                return result;
            }

            private static Dictionary<int, PARAM_Hack.Row> Index(PARAM_Hack? p)
            {
                var d = new Dictionary<int, PARAM_Hack.Row>();
                if (p != null) foreach (var r in p.Rows) d.TryAdd(r.ID, r);
                return d;
            }

            /// <summary>Rows are at least as big as the Sekiro paramdef says (guards against another game's param).</summary>
            private static bool Fits(PARAM_Hack p, int rowSize) => p.DetectedSize < 0 || p.DetectedSize >= rowSize;

            private static int[]? ReadIds(PARAM_Hack p, PARAM_Hack.Row row, int offset0, int rowSize)
            {
                if (!Fits(p, rowSize)) return null;
                var br = p.GetRowReader(row);
                long start = br.Position;
                if (start + rowSize > br.Length) return null;
                var ids = new List<int>(SekiroParamOffsets.SpEffectIdCount);
                for (int i = 0; i < SekiroParamOffsets.SpEffectIdCount; i++)
                {
                    int sp = br.GetInt32(start + offset0 + 4 * i);
                    if (sp > 0) ids.Add(sp);
                }
                return ids.ToArray();
            }
        }
    }
}
