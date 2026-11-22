#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DSAnimStudio;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// TAE actions in the clipboard format of DS Anim Studio 4.9 (Newtonsoft JSON of <c>DSAnimStudio.TaeEditor.TaeClipboardContents</c>:
    /// <c>EventClips</c> with EventType, EventStartTime / EventEndTime, EventRow, base64 EventParamBytes). DSAS 5 (this app)
    /// copies a hex "DSCB" blob instead, so neither could paste the other's events.
    /// <list type="bullet">
    /// <item><see cref="TryRead"/>: Ctrl+V in the graph accepts the 4.9 JSON (EventRow → track, param bytes as they are).</item>
    /// <item><see cref="Write"/>: Action Graph menu, export the current animation's actions as 4.9 JSON (clipboard or file).</item>
    /// </list>
    /// Same game, same TAE template: the parameter bytes need no conversion. 4.9's EventGroup is not used (5.x has tracks).
    /// </summary>
    public static class DsasLegacyClipboard
    {
        private const string Asm = "DS ANIM STUDIO";

        public static bool LooksLikeLegacy(string? text) =>
            text != null && text.TrimStart().StartsWith("{", StringComparison.Ordinal) && text.Contains("EventClips", StringComparison.Ordinal);

        /// <summary>Parses 4.9 JSON into new actions (template applied when known). <paramref name="warnings"/>: types / sizes the template does not know.</summary>
        public static bool TryRead(string? text, DSAProj? proj, out List<DSAProj.Action> actions, out List<string> warnings)
        {
            actions = new List<DSAProj.Action>();
            warnings = new List<string>();
            if (!LooksLikeLegacy(text)) return false;
            JObject root;
            try
            {
                using var reader = new JsonTextReader(new StringReader(text!)) { DateParseHandling = DateParseHandling.None };
                root = JObject.Load(reader);
            }
            catch (JsonException) { return false; }

            var clips = root["EventClips"] is JObject wrapped ? wrapped["$values"] as JArray : root["EventClips"] as JArray;
            if (clips == null) return false;
            foreach (var c in clips.OfType<JObject>())
            {
                var act = new DSAProj.Action
                {
                    Type = (int?)c["EventType"] ?? 0,
                    Unk04 = (int?)c["EventUnk04"] ?? 0,
                    ParameterBytes = ReadBytes(c["EventParamBytes"]),
                };
                act.StartTime = (float?)c["EventStartTime"] ?? 0;
                act.EndTime = (float?)c["EventEndTime"] ?? act.StartTime;
                act.TrackIndex = Math.Max(0, (int?)c["EventRow"] ?? 0);
                act.OriginalParamBytesLength = act.ParameterBytes.Length;

                if (proj?.Template != null && proj.Template.ContainsKey(act.Type))
                {
                    var tmpl = proj.Template[act.Type];
                    int expected = tmpl.GetDefaultBytes().Length;
                    if (expected != act.ParameterBytes.Length)
                        warnings.Add($"type {act.Type} at {act.StartTime:0.###}s: {act.ParameterBytes.Length} parameter bytes, this game's template expects {expected}");
                    act.ApplyTemplate(tmpl);
                }
                else warnings.Add($"type {act.Type} at {act.StartTime:0.###}s: unknown to this game's template (pasted as raw bytes)");
                actions.Add(act);
            }
            return actions.Count > 0;
        }

        private static byte[] ReadBytes(JToken? t)
        {
            string? b64 = t switch
            {
                JObject o => (string?)o["$value"],
                JValue v => (string?)v,
                _ => null,
            };
            if (string.IsNullOrEmpty(b64)) return Array.Empty<byte>();
            try { return Convert.FromBase64String(b64); }
            catch (FormatException) { return Array.Empty<byte>(); }
        }

        /// <summary>4.9 clipboard JSON of <paramref name="actions"/> (parameters saved to bytes first). Paste it in DSAS 4.9 with Ctrl+V.</summary>
        public static string Write(IEnumerable<DSAProj.Action> actions)
        {
            var list = actions.Select(a => a.GetClone()) // GetClone saves edited parameters to bytes
                              .OrderBy(a => a.TrackIndex).ThenBy(a => a.StartTime).ToList();
            var sw = new StringWriter(CultureInfo.InvariantCulture);
            using (var w = new JsonTextWriter(sw) { Formatting = Formatting.Indented, Indentation = 2, FloatFormatHandling = FloatFormatHandling.DefaultValue })
            {
                w.WriteStartObject();
                w.WritePropertyName("$type"); w.WriteValue($"DSAnimStudio.TaeEditor.TaeClipboardContents, {Asm}");
                w.WritePropertyName("EventClips");
                w.WriteStartObject();
                w.WritePropertyName("$type"); w.WriteValue($"System.Collections.Generic.List`1[[DSAnimStudio.TaeEditor.TaeClipboardContents+Clip, {Asm}]], System.Private.CoreLib");
                w.WritePropertyName("$values");
                w.WriteStartArray();
                foreach (var a in list)
                {
                    w.WriteStartObject();
                    w.WritePropertyName("$type"); w.WriteValue($"DSAnimStudio.TaeEditor.TaeClipboardContents+Clip, {Asm}");
                    w.WritePropertyName("EventType"); w.WriteValue(a.Type);
                    w.WritePropertyName("EventUnk04"); w.WriteValue(a.Unk04);
                    w.WritePropertyName("EventStartTime"); w.WriteRawValue(Float(a.StartTime));
                    w.WritePropertyName("EventEndTime"); w.WriteRawValue(Float(a.EndTime));
                    w.WritePropertyName("EventRow"); w.WriteValue(a.TrackIndex);
                    w.WritePropertyName("EventParamBytes");
                    w.WriteStartObject();
                    w.WritePropertyName("$type"); w.WriteValue("System.Byte[], System.Private.CoreLib");
                    w.WritePropertyName("$value"); w.WriteValue(Convert.ToBase64String(a.ParameterBytes ?? Array.Empty<byte>()));
                    w.WriteEndObject();
                    w.WritePropertyName("EventGroup");
                    w.WriteStartObject();
                    w.WritePropertyName("$type"); w.WriteValue("SoulsAssetPipeline.Animation.TAE+EventGroup, SoulsAssetPipeline");
                    w.WritePropertyName("GroupData");
                    w.WriteStartObject();
                    w.WritePropertyName("$type"); w.WriteValue("SoulsAssetPipeline.Animation.TAE+EventGroup+EventGroupDataStruct, SoulsAssetPipeline");
                    foreach (var f in new[] { "DataType", "CutsceneEntityType", "CutsceneEntityIDPart1", "CutsceneEntityIDPart2", "Area", "Block" })
                    {
                        w.WritePropertyName(f); w.WriteValue(0);
                    }
                    w.WriteEndObject();
                    w.WritePropertyName("GroupType"); w.WriteValue(a.Type); // 4.9 groups each event by its own type
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
                w.WritePropertyName("StartRow"); w.WriteValue(0);
                w.WritePropertyName("StartTime"); w.WriteRawValue("0.0");
                w.WritePropertyName("IsBigEndian"); w.WriteValue(false);
                w.WriteEndObject();
            }
            return sw.ToString();
        }

        /// <summary>Float as .NET Core prints it (shortest round-trip, e.g. 0.33333334), always with a decimal point.</summary>
        private static string Float(float f)
        {
            string s = f.ToString("R", CultureInfo.InvariantCulture);
            return s.Contains('.') || s.Contains('E') ? s : s + ".0";
        }
    }
}
