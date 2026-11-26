using System.Windows.Input;

namespace WaveGate.Overlay.Hotkeys;

/// <summary>A key plus modifiers, stored in board.json as e.g. "Ctrl+Alt+F1".</summary>
public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    /// <summary>
    /// A global hotkey swallows the key system-wide, so plain typing keys must
    /// carry Ctrl, Alt or Win. Function keys and the numpad may stand alone.
    /// </summary>
    public bool IsSafeGlobal =>
        (Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0 ||
        Key is >= Key.F1 and <= Key.F24 ||
        Key is >= Key.NumPad0 and <= Key.Divide;

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var modifiers = ModifierKeys.None;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        foreach (var part in parts[..^1])
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModifierKeys.Control; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "win" or "windows": modifiers |= ModifierKeys.Windows; break;
                default: return false;
            }
        }

        var keyName = parts[^1];
        if (keyName.Length == 1 && char.IsDigit(keyName[0])) keyName = "D" + keyName; // "1" -> Key.D1
        // Enum.TryParse also accepts raw numbers, which would map to unrelated keys.
        if (int.TryParse(keyName, out _) || !Enum.TryParse(keyName, ignoreCase: true, out Key key) || key == Key.None)
            return false;

        hotkey = new Hotkey(modifiers, key);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(Key is >= Key.D0 and <= Key.D9 ? ((int)(Key - Key.D0)).ToString() : Key.ToString());
        return string.Join("+", parts);
    }
}
