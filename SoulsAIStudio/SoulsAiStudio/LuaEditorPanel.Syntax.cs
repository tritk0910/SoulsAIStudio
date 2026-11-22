#nullable enable
using System.Collections.Generic;
using ImGuiNET;
using SoulsAiStudio.Core.Lua.Syntax;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// Lua syntax errors in the editor (<see cref="LuaSyntaxChecker"/>, run with the debounced analysis): red underline +
    /// gutter marker on the error (and on the line where an <c>end</c> is probably missing), a status-line button that
    /// jumps there, and a warning when a script with an error is saved (the game would not load it).
    /// </summary>
    public sealed partial class LuaEditorPanel
    {
        private static void ApplySyntaxError(OpenScript o, LuaSyntaxError? e)
        {
            o.SyntaxError = e;
            var list = new List<(int, int, int, bool, string)>();
            if (e != null)
            {
                list.Add((e.Line, e.StartColumn, e.EndColumn, false, "Syntax error: " + e.Message));
                if (e.HintLine >= 0 && e.Hint != null)
                    list.Add((e.HintLine, 0, e.HintEndColumn, false, "Syntax error: " + e.Hint));
            }
            o.Editor.SetDiagnostics(list);
        }

        /// <summary>Where to look first: the likely missing 'end' if known, else the error itself.</summary>
        private static int SyntaxErrorLine(LuaSyntaxError e) => e.HintLine >= 0 ? e.HintLine : e.Line;

        private void BuildSyntaxErrorButton(OpenScript o)
        {
            if (o.SyntaxError is not { } e) return;
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Error);
            bool click = ImGui.SmallButton($"Syntax error (line {SyntaxErrorLine(e) + 1})##sas_syntax");
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(("Line " + (e.Line + 1) + ": " + e.Message +
                                  (e.Hint != null ? "\nLine " + (e.HintLine + 1) + ": " + e.Hint : "") +
                                  "\n\nThe game cannot load this script until it is fixed. Click: go to the line.").Replace("%", "%%"));
            if (click) o.Editor.GoToLine(SyntaxErrorLine(e));
        }

        private void WarnIfSavedWithSyntaxError(OpenScript o, string savedText)
        {
            if (LuaSyntaxChecker.Check(savedText) is not { } e) return;
            _app.Notify($"{o.Script.FileName} was saved WITH A SYNTAX ERROR (line {SyntaxErrorLine(e) + 1}: {e.Hint ?? e.Message}). " +
                        "The game cannot load this script until it is fixed.");
        }
    }
}
