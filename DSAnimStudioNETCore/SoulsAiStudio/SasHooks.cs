#nullable enable
using System;
using System.IO;
using ImGuiNET;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// The only seam between DSAS and Souls AI Studio. Every DSAS edit is a one-line call into this class marked
    /// <c>// [SAS]</c> (CLAUDE.md §6). Keep these methods tiny; real work lives in <see cref="SasApp"/> and Core.
    /// </summary>
    public static class SasHooks
    {
        private static SasApp? _app;
        public static SasApp App => _app ??= new SasApp();

        /// <summary>Called from <c>OSD.Build</c> after DSAS windows; draws our windows.</summary>
        public static void BuildWindows(ref bool anyFieldFocused)
        {
            try { App.BuildWindows(ref anyFieldFocused); }
            catch (Exception ex) { App.ReportError("UI error", ex); }
        }

        /// <summary>Called from <c>MenuBar.BuildMenuBar</c> before the DSAS menus.</summary>
        public static void BuildMenus(ref bool anyItemFocused, ref bool isAnyMenuExpanded)
        {
            try { App.BuildMenus(ref anyItemFocused, ref isAnyMenuExpanded); }
            catch (Exception ex) { App.ReportError("Menu error", ex); }
        }

        /// <summary>Called from the DSAS font build: adds Vietnamese/Latin-extended glyphs (DSAS's DS3 glyph list lacks them).</summary>
        public static void AddExtraGlyphs(ImFontGlyphRangesBuilderPtr builder)
        {
            static void Range(ImFontGlyphRangesBuilderPtr b, int from, int to)
            {
                for (int c = from; c <= to; c++) b.AddChar((ushort)c);
            }
            Range(builder, 0x00A0, 0x00FF); // Latin-1 supplement
            Range(builder, 0x0100, 0x024F); // Latin Extended-A/B (ă, đ, ơ, ư)
            Range(builder, 0x0300, 0x036F); // combining diacritics
            Range(builder, 0x1EA0, 0x1EF9); // Vietnamese precomposed (ạ … ỹ)
            Range(builder, 0x2190, 0x21FF); // arrows used in the UI (→ ←)
            Range(builder, 0x25A0, 0x25FF); // geometric shapes (▶ ●)
        }

        /// <summary>
        /// Replaces DSAS's in-place <c>File.WriteAllBytes</c> when saving an anibnd: refuses writes into the game
        /// folder (outside <c>mods\</c>), backs up the old file, and writes atomically.
        /// </summary>
        public static void WriteBinder(string path, byte[] bytes)
        {
            string? why = App.WhyWriteIsBlocked(path);
            if (why != null)
            {
                DSAnimStudio.ImguiOSD.DialogManager.DialogOK("Save blocked (Souls AI Studio)", why);
                throw new IOException(why);
            }
            App.BackupBeforeExternalWrite(path);
            SoulsAiStudio.Core.IO.AtomicFile.WriteAllBytes(path, bytes);
        }
    }
}
