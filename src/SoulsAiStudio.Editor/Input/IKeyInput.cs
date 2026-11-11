using ImGuiNET;

namespace SoulsAiStudio.Editor.Input;

/// <summary>Keys the editor reacts to.</summary>
public enum EditorKey
{
    None = 0,
    Left, Right, Up, Down,
    Home, End, PageUp, PageDown,
    Backspace, Delete, Enter, Tab, Escape,
    A, C, V, X, Y, Z,
    F,      // Ctrl+F  find
    G,      // Ctrl+G  go to line
    F3,     // F3 / Shift+F3  find next / previous
    Count,
}

/// <summary>
/// Keyboard source for <see cref="LuaCodeEditor"/>. Polled once per <see cref="LuaCodeEditor.Render"/>,
/// only while the editor has keyboard focus.
/// <para>
/// HOST CONTRACT (DSAS): DSAS's ImGuiRenderer forwards only modifiers, arrows, Home/End, PageUp/Down,
/// Delete, Backspace, Enter, Escape, Tab and A/C/V/X/Y/Z to ImGui. <see cref="ImGuiKeyInput"/> covers
/// those. The host MUST additionally report <see cref="EditorKey.F"/>, <see cref="EditorKey.G"/> and
/// <see cref="EditorKey.F3"/> (e.g. subclass <see cref="ImGuiKeyInput"/> and OR in MonoGame
/// <c>Main.Input.KeyDown(Keys.F)</c>), or forward those keys to ImGui via <c>io.AddKeyEvent</c>.
/// Typed characters come from ImGui's input queue (DSAS feeds it from <c>Window.TextInput</c>; '\t' is
/// filtered there, Tab is handled as a key here).
/// </para>
/// </summary>
public interface IKeyInput
{
    bool Ctrl { get; }
    bool Shift { get; }
    bool Alt { get; }

    /// <summary>True on the frame the key went down AND on OS-style auto-repeat frames.</summary>
    bool IsPressed(EditorKey key);

    /// <summary>
    /// Copies the characters typed this frame into <paramref name="destination"/> and returns the count.
    /// Must not allocate. Control chars are filtered by the editor.
    /// </summary>
    int ReadTypedChars(Span<char> destination);
}

/// <summary>Default <see cref="IKeyInput"/> that reads ImGui's io (key state + input character queue).</summary>
public class ImGuiKeyInput : IKeyInput
{
    public static readonly ImGuiKeyInput Instance = new();

    public virtual bool Ctrl => ImGui.GetIO().KeyCtrl;
    public virtual bool Shift => ImGui.GetIO().KeyShift;
    public virtual bool Alt => ImGui.GetIO().KeyAlt;

    public virtual bool IsPressed(EditorKey key)
    {
        var k = Map(key);
        return k != ImGuiKey.None && ImGui.IsKeyPressed(k, true);
    }

    public virtual int ReadTypedChars(Span<char> destination)
    {
        var queue = ImGui.GetIO().InputQueueCharacters;
        int n = Math.Min(queue.Size, destination.Length);
        for (int i = 0; i < n; i++)
            destination[i] = (char)queue[i];
        return n;
    }

    public static ImGuiKey Map(EditorKey key) => key switch
    {
        EditorKey.Left => ImGuiKey.LeftArrow,
        EditorKey.Right => ImGuiKey.RightArrow,
        EditorKey.Up => ImGuiKey.UpArrow,
        EditorKey.Down => ImGuiKey.DownArrow,
        EditorKey.Home => ImGuiKey.Home,
        EditorKey.End => ImGuiKey.End,
        EditorKey.PageUp => ImGuiKey.PageUp,
        EditorKey.PageDown => ImGuiKey.PageDown,
        EditorKey.Backspace => ImGuiKey.Backspace,
        EditorKey.Delete => ImGuiKey.Delete,
        EditorKey.Enter => ImGuiKey.Enter,
        EditorKey.Tab => ImGuiKey.Tab,
        EditorKey.Escape => ImGuiKey.Escape,
        EditorKey.A => ImGuiKey.A,
        EditorKey.C => ImGuiKey.C,
        EditorKey.V => ImGuiKey.V,
        EditorKey.X => ImGuiKey.X,
        EditorKey.Y => ImGuiKey.Y,
        EditorKey.Z => ImGuiKey.Z,
        EditorKey.F => ImGuiKey.F,
        EditorKey.G => ImGuiKey.G,
        EditorKey.F3 => ImGuiKey.F3,
        _ => ImGuiKey.None,
    };
}
