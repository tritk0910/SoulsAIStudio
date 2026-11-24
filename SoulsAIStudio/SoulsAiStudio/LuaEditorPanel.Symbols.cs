// [SAS] Souls AI Studio: F2 rename, Ctrl+Click go-to-declaration and the formatter glue for the Lua editor panel.
using System;
using System.Collections.Generic;
using System.Linq;
using SoulsAiStudio.Core.Lua.Analysis;
using SoulsAiStudio.Core.Lua.Syntax;
using SoulsAiStudio.Editor;

namespace SoulsAiStudio.App
{
    public sealed partial class LuaEditorPanel
    {
        /// <summary>The local variable / parameter at (line, column), as the editor's symbol; null for globals, fields, keywords.</summary>
        private static EditorSymbol? SymbolAt(OpenScript o, int line, int column)
        {
            if (o.Doc == null) return null;
            if (o.Scopes == null || o.ScopesVersion != o.Editor.Version)
            {
                o.Scopes = LuaScopes.Build(o.Editor.Text);
                o.ScopesVersion = o.Editor.Version;
            }
            var v = o.Scopes.VariableAt(line, column);
            if (v == null) return null;
            static EditorSpan Span(Core.Lua.Syntax.LuaToken t) => new(t.Line, t.Column, t.Length);
            return new EditorSymbol(v.Name, Span(v.Declaration), v.Occurrences.Select(Span).ToList(), (o.Scopes, v));
        }

        private static string? ValidateRename(EditorSymbol sym, string newName) =>
            sym.Tag is ValueTuple<LuaScopes, LuaVariable> t ? t.Item1.CheckRename(t.Item2, newName) : null;

        /// <summary>Formats the script in the active tab (menu / Shift+Alt+F).</summary>
        public void FormatActive()
        {
            if (_active?.Doc != null) FormatDocument(_active, notify: true);
        }

        /// <summary>
        /// Formats <paramref name="o"/>'s text (LuaFormatter) as ONE undo step, replacing only the lines that changed.
        /// Returns true when the text changed. A script with a syntax error is left alone.
        /// </summary>
        private bool FormatDocument(OpenScript o, bool notify)
        {
            if (o.Doc == null || o.Editor.ReadOnly) return false;
            string text = o.Editor.Text;
            string? formatted = LuaFormatter.Format(text);
            if (formatted == null)
            {
                if (notify) _app.Notify($"Format skipped for {o.Script.FileName}: the script has a syntax error.");
                return false;
            }
            if (formatted == text) return false;

            var a = text.Replace("\r\n", "\n").Split('\n');
            var b = formatted.Replace("\r\n", "\n").Split('\n');
            int p = 0;
            while (p < a.Length && p < b.Length && a[p] == b[p]) p++;
            int s = 0;
            while (s < a.Length - p && s < b.Length - p && a[a.Length - 1 - s] == b[b.Length - 1 - s]) s++;
            // keep one common line around the change so deleted lines don't leave an empty one behind
            if (p > 0) p--; else if (s > 0) s--;
            int aEnd = a.Length - 1 - s, bEnd = b.Length - 1 - s;
            if (aEnd < p || bEnd < p) { p = 0; s = 0; aEnd = a.Length - 1; bEnd = b.Length - 1; }
            string replacement = string.Join("\n", b.Skip(p).Take(bEnd - p + 1));
            return o.Editor.ApplyEdit(p, 0, aEnd, a[aEnd].Length, replacement, keepCursor: true);
        }
    }
}
