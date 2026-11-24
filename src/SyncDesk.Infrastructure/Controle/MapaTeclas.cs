namespace SyncDesk.Controle;

internal static class MapaTeclas
{
    private static readonly Dictionary<string, (ushort Vk, bool Estendida)> Teclas = new(StringComparer.Ordinal)
    {
        ["Backspace"] = (0x08, false), ["Tab"] = (0x09, false), ["Enter"] = (0x0D, false),
        ["NumpadEnter"] = (0x0D, true), ["CapsLock"] = (0x14, false),
        ["Escape"] = (0x1B, false), ["Space"] = (0x20, false),
        ["PageUp"] = (0x21, true), ["PageDown"] = (0x22, true), ["End"] = (0x23, true),
        ["Home"] = (0x24, true), ["ArrowLeft"] = (0x25, true), ["ArrowUp"] = (0x26, true),
        ["ArrowRight"] = (0x27, true), ["ArrowDown"] = (0x28, true),
        ["Insert"] = (0x2D, true), ["Delete"] = (0x2E, true),
        ["ShiftLeft"] = (0xA0, false), ["ShiftRight"] = (0xA1, false),
        ["ControlLeft"] = (0xA2, false), ["ControlRight"] = (0xA3, true),
        ["AltLeft"] = (0xA4, false), ["AltRight"] = (0xA5, true),
        ["Semicolon"] = (0xBA, false), ["Equal"] = (0xBB, false), ["Comma"] = (0xBC, false),
        ["Minus"] = (0xBD, false), ["Period"] = (0xBE, false), ["Slash"] = (0xBF, false),
        ["Backquote"] = (0xC0, false), ["BracketLeft"] = (0xDB, false), ["Backslash"] = (0xDC, false),
        ["BracketRight"] = (0xDD, false), ["Quote"] = (0xDE, false), ["IntlBackslash"] = (0xE2, false),
        ["NumpadMultiply"] = (0x6A, false), ["NumpadAdd"] = (0x6B, false), ["NumpadSubtract"] = (0x6D, false),
        ["NumpadDecimal"] = (0x6E, false), ["NumpadDivide"] = (0x6F, true)
    };

    static MapaTeclas()
    {
        for (ushort i = 'A'; i <= 'Z'; i++) Teclas.Add($"Key{(char)i}", (i, false));
        for (int i = 0; i < 10; i++)
        {
            Teclas.Add($"Digit{i}", ((ushort)('0' + i), false));
            Teclas.Add($"Numpad{i}", ((ushort)(0x60 + i), false));
        }
        for (int i = 1; i <= 12; i++) Teclas.Add($"F{i}", ((ushort)(0x6F + i), false));
    }

    public static bool TentarMapear(string codigo, out (ushort Vk, bool Estendida) tecla) =>
        Teclas.TryGetValue(codigo, out tecla);
}
