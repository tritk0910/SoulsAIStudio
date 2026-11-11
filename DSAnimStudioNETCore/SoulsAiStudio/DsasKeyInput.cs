#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Input;
using SoulsAiStudio.Editor.Input;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// Editor key source for DSAS (editor host contract, see <see cref="IKeyInput"/>):
    /// <list type="bullet">
    /// <item>Typed characters come from the game form's <c>KeyPress</c> (WM_CHAR). Reading ImGui's
    /// <c>io.InputQueueCharacters</c> returned nothing in DSAS (ImGui.NET 1.87.3 managed vs 1.88 native struct layout).</item>
    /// <item>Modifiers and F/G/F3/S come from MonoGame polling (DSAS never forwards F/G/F3 to ImGui).</item>
    /// <item>Navigation/editing keys still use ImGui's key state (<c>ImGui.IsKeyPressed</c>, with auto-repeat).</item>
    /// </list>
    /// Call <see cref="NewFrame"/> once per frame before rendering editors.
    /// </summary>
    public sealed class DsasKeyInput : ImGuiKeyInput
    {
        private KeyboardState _prev, _now;
        private readonly Queue<char> _pending = new();
        private readonly char[] _frameChars = new char[256];
        private int _frameCount;
        private bool _hooked;
        private int _keyDownsSinceChar;

        public void NewFrame()
        {
            _prev = _now;
            _now = Keyboard.GetState();
            HookOnce();
            _frameCount = 0;
            while (_pending.Count > 0 && _frameCount < _frameChars.Length) _frameChars[_frameCount++] = _pending.Dequeue();
        }

        private void HookOnce()
        {
            if (_hooked || DSAnimStudio.Main.WinForm == null) return;
            // After DSAS opens a document, every WM_CHAR arrives twice (seen in the find bar and ImGui's own InputText).
            // A real character always follows a WM_KEYDOWN (incl. VK_PACKET from IMEs like Unikey), so accept at most one
            // KeyPress per KeyDown; the duplicate has no key-down of its own and is dropped.
            DSAnimStudio.Main.WinForm.KeyDown += (_, _) => _keyDownsSinceChar++;
            DSAnimStudio.Main.WinForm.KeyPress += (_, e) =>
            {
                if (_keyDownsSinceChar == 0) return;
                _keyDownsSinceChar = 0;
                if (_pending.Count < 1024) _pending.Enqueue(e.KeyChar);
            };
            _hooked = true;
        }

        private bool Down(Keys k) => _now.IsKeyDown(k);
        private bool Edge(Keys k) => _now.IsKeyDown(k) && !_prev.IsKeyDown(k);

        public override bool Ctrl => Down(Keys.LeftControl) || Down(Keys.RightControl);
        public override bool Shift => Down(Keys.LeftShift) || Down(Keys.RightShift);
        public override bool Alt => Down(Keys.LeftAlt) || Down(Keys.RightAlt);

        public bool CtrlS => Ctrl && Edge(Keys.S);

        public override bool IsPressed(EditorKey key) => key switch
        {
            EditorKey.F => Edge(Keys.F),
            EditorKey.G => Edge(Keys.G),
            EditorKey.F3 => Edge(Keys.F3),
            _ => base.IsPressed(key),
        };

        /// <summary>Characters typed since the last frame. Ctrl-combos arrive as control chars and are filtered by the editor.</summary>
        public override int ReadTypedChars(Span<char> destination)
        {
            int n = Math.Min(_frameCount, destination.Length);
            _frameChars.AsSpan(0, n).CopyTo(destination);
            return n;
        }
    }
}
