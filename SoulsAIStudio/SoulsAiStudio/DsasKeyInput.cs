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
        private CharHook? _charHook;

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
            // Read WM_CHAR straight from the window procedure. WinForms' KeyPress fires for BOTH WM_IME_CHAR and the
            // WM_CHAR that DefWindowProc derives from it, so with a Vietnamese IME (Unikey/Telex) every character
            // arrived twice. WM_CHAR alone carries each committed character exactly once.
            _charHook = new CharHook(DSAnimStudio.Main.WinForm.Handle, c => { if (_pending.Count < 1024) _pending.Enqueue(c); });
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

    /// <summary>
    /// Turns the Windows IME (e.g. Microsoft Vietnamese Telex) off while the Lua editor has focus. The IME holds
    /// typed text in a composition string until a word is committed, and the editor can't draw that string, so typing
    /// looked delayed until Space/Enter. Hook-based tools like Unikey still work (they send plain characters).
    /// </summary>
    internal static class ImeGuard
    {
        [System.Runtime.InteropServices.DllImport("imm32.dll")] private static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hIMC);
        private static IntPtr _saved;
        private static bool _disabled;

        public static void SetEditorFocused(IntPtr hwnd, bool focused, bool allowIme)
        {
            bool wantDisabled = focused && !allowIme;
            if (wantDisabled == _disabled || hwnd == IntPtr.Zero) return;
            if (wantDisabled) _saved = ImmAssociateContext(hwnd, IntPtr.Zero);
            else ImmAssociateContext(hwnd, _saved);
            _disabled = wantDisabled;
        }
    }

    /// <summary>Subclasses the game window to observe WM_CHAR (0x0102) without changing its handling.</summary>
    internal sealed class CharHook : System.Windows.Forms.NativeWindow
    {
        private const int WM_CHAR = 0x0102;
        private readonly Action<char> _onChar;

        public CharHook(IntPtr handle, Action<char> onChar)
        {
            _onChar = onChar;
            AssignHandle(handle);
        }

        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            if (m.Msg == WM_CHAR) _onChar((char)(long)m.WParam);
            base.WndProc(ref m);
        }
    }
}
