#nullable enable
using System;
using System.Collections.Generic;
using DSAnimStudio;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// Keeps the Toolbox [Lighting] values across launches. DSAS saves only the skybox/cubemap and Skybox Brightness in its
    /// config; everything else (light direction, light/ambient multipliers, tonemap, SSS…) used to reset on every launch.
    /// Values live in <see cref="SasSettings.ViewportLighting"/>; changes are picked up by polling (no DSAS edits).
    /// </summary>
    public sealed class ViewportLightingStore
    {
        private sealed record Field(string Name, Func<float> Get, Action<float> Set);

        private static float B(bool v) => v ? 1 : 0;

        private static readonly Field[] Fields =
        {
            new("LightRotationH", () => ViewportEnvironment.LightRotationH, v => ViewportEnvironment.LightRotationH = v),
            new("LightRotationV", () => ViewportEnvironment.LightRotationV, v => ViewportEnvironment.LightRotationV = v),
            new("LightFollowsCamera", () => B(GFX.FlverLightFollowsCamera), v => GFX.FlverLightFollowsCamera = v != 0),
            new("AutoRotateLight", () => B(GFX.FlverAutoRotateLight), v => GFX.FlverAutoRotateLight = v != 0),
            new("DirectLightMult", () => ViewportEnvironment.FlverDirectLightMult, v => ViewportEnvironment.FlverDirectLightMult = v),
            new("IndirectLightMult", () => ViewportEnvironment.FlverIndirectLightMult, v => ViewportEnvironment.FlverIndirectLightMult = v),
            new("AmbientLightMult", () => ViewportEnvironment.AmbientLightMult, v => ViewportEnvironment.AmbientLightMult = v),
            new("SpecularPowerMult", () => GFX.SpecularPowerMult, v => GFX.SpecularPowerMult = v),
            new("LdotNPower", () => GFX.LdotNPower, v => GFX.LdotNPower = v),
            new("EmissiveMult", () => ViewportEnvironment.FlverEmissiveMult, v => ViewportEnvironment.FlverEmissiveMult = v),
            new("DirectDiffuseMult", () => ViewportEnvironment.DirectDiffuseMult, v => ViewportEnvironment.DirectDiffuseMult = v),
            new("DirectSpecularMult", () => ViewportEnvironment.DirectSpecularMult, v => ViewportEnvironment.DirectSpecularMult = v),
            new("IndirectDiffuseMult", () => ViewportEnvironment.IndirectDiffuseMult, v => ViewportEnvironment.IndirectDiffuseMult = v),
            new("IndirectSpecularMult", () => ViewportEnvironment.IndirectSpecularMult, v => ViewportEnvironment.IndirectSpecularMult = v),
            new("DiffuseMapBrightness", () => ViewportEnvironment.DiffuseMapBrightness, v => ViewportEnvironment.DiffuseMapBrightness = v),
            new("SpecularMapBrightness", () => ViewportEnvironment.SpecularMapBrightness, v => ViewportEnvironment.SpecularMapBrightness = v),
            new("SSSMult", () => ViewportEnvironment.SSSMult, v => ViewportEnvironment.SSSMult = v),
            new("UseTonemap", () => B(GFX.UseTonemap), v => GFX.UseTonemap = v != 0),
            new("TonemapBrightness", () => ViewportEnvironment.FlverSceneBrightness, v => ViewportEnvironment.FlverSceneBrightness = v),
            new("TonemapContrast", () => ViewportEnvironment.FlverSceneContrast, v => ViewportEnvironment.FlverSceneContrast = v),
        };

        private readonly SasSettings _settings;
        private readonly float[] _last = new float[Fields.Length];
        private int _frame;

        public ViewportLightingStore(SasSettings settings) => _settings = settings;

        /// <summary>Restores the saved values (call after the lighting preset so the user's own values win).</summary>
        public void Restore()
        {
            var saved = _settings.ViewportLighting;
            if (saved != null)
                foreach (var f in Fields)
                    if (saved.TryGetValue(f.Name, out float v) && float.IsFinite(v)) f.Set(v);
            for (int i = 0; i < Fields.Length; i++) _last[i] = Fields[i].Get();
        }

        /// <summary>Every ~0.5 s: if a value changed (and the mouse is not still dragging a slider), save all of them.</summary>
        public void Tick()
        {
            if (++_frame < 30) return;
            _frame = 0;
            if (ImGuiNET.ImGui.IsMouseDown(ImGuiNET.ImGuiMouseButton.Left)) return;
            bool changed = false;
            for (int i = 0; i < Fields.Length; i++)
            {
                float v = Fields[i].Get();
                if (v != _last[i]) { _last[i] = v; changed = true; }
            }
            if (!changed) return;
            var dict = new Dictionary<string, float>(Fields.Length);
            for (int i = 0; i < Fields.Length; i++) dict[Fields[i].Name] = _last[i];
            _settings.ViewportLighting = dict;
            _settings.Save();
        }
    }
}
