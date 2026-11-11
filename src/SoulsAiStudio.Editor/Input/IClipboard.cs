using ImGuiNET;

namespace SoulsAiStudio.Editor.Input;

/// <summary>Clipboard access. The default goes through ImGui (which DSAS wires to the OS clipboard).</summary>
public interface IClipboard
{
    string? GetText();
    void SetText(string text);
}

/// <summary>Clipboard via ImGui.GetClipboardText / SetClipboardText.</summary>
public sealed class ImGuiClipboard : IClipboard
{
    public static readonly ImGuiClipboard Instance = new();

    public unsafe string? GetText()
    {
        if (ImGuiNative.igGetClipboardText() == null)
            return null;
        return ImGui.GetClipboardText();
    }

    public void SetText(string text) => ImGui.SetClipboardText(text);
}

/// <summary>In-memory clipboard (tests / headless use).</summary>
public sealed class MemoryClipboard : IClipboard
{
    public string? Text { get; set; }
    public string? GetText() => Text;
    public void SetText(string text) => Text = text;
}
