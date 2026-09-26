using Homie.Native;
using Windows.System;

namespace Homie.Services;

/// <summary>Подписи для горячих клавиш: «Ctrl + Alt + 1», «Сценарий: Ночь».</summary>
public static class HotkeyText
{
    public static string Format(uint modifiers, uint key)
    {
        var parts = new List<string>();
        if ((modifiers & Win32.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & Win32.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & Win32.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & Win32.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(KeyName(key));
        return string.Join(" + ", parts);
    }

    public static string Describe(HotkeyTarget target, string name) =>
        target == HotkeyTarget.Scenario ? $"Сценарий: {name}" : $"Вкл/выкл: {name}";

    private static string KeyName(uint key)
    {
        var vk = (VirtualKey)key;
        return vk switch
        {
            >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((char)key).ToString(),
            >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9 => $"Num {key - (uint)VirtualKey.NumberPad0}",
            >= VirtualKey.A and <= VirtualKey.Z => ((char)key).ToString(),
            _ => vk.ToString(),
        };
    }

    /// <summary>Клавиши-модификаторы сами по себе сочетанием не считаются.</summary>
    public static bool IsModifier(VirtualKey key) => key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
        or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
        or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
        or VirtualKey.LeftWindows or VirtualKey.RightWindows;
}
