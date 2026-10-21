using Flamingle.App.Native;
using Flamingle.Core.Hotkeys;
using Flamingle.Core.Settings;

namespace Flamingle.App.Services;

/// <summary>
/// Registers Flamingle's global hotkeys from settings: "edit layout" and the nine slot toggles.
/// Construct and drive on the UI thread (see <see cref="MessageWindow"/>).
/// </summary>
internal sealed class HotkeyService : IDisposable
{
    private const int LayoutMenuId = 3;
    private const int FirstSlotId = 10;   // slot n → FirstSlotId + n

    private readonly MessageWindow _window;
    private readonly List<int> _registered = [];

    public HotkeyService()
    {
        _window = new MessageWindow("FlamingleHotkeyWindow");
        _window.HotkeyPressed += OnHotkeyPressed;
    }

    public event Action? LayoutMenuRequested;
    public event Action<int>? SlotRequested;

    /// <summary>Let go of every hotkey for now (while the settings window records a new combo); <see cref="Apply"/>
    /// takes them back.</summary>
    public void Suspend()
    {
        foreach (var id in _registered) _window.UnregisterHotkey(id);
        _registered.Clear();
    }

    /// <summary>(Re-)register every hotkey. Returns the combos the OS refused (usually: another app owns them).</summary>
    public IReadOnlyList<string> Apply(AppSettings settings)
    {
        Suspend();

        var failed = new List<string>();
        Register(LayoutMenuId, settings.LayoutMenuHotkey, failed);

        if (!string.IsNullOrWhiteSpace(settings.SlotHotkeyModifiers))
        {
            for (var n = 1; n <= 9; n++)
                Register(FirstSlotId + n, $"{settings.SlotHotkeyModifiers}+{n}", failed);
        }
        return failed;
    }

    /// <summary>A display string for the slot hotkey, e.g. <c>"Alt+Shift+2"</c>, or null if disabled.</summary>
    public static string? SlotHotkeyText(AppSettings settings, int slot)
        => slot is >= 1 and <= 9 ? Hotkey.TryParse($"{settings.SlotHotkeyModifiers}+{slot}")?.ToString() : null;

    private void Register(int id, string? text, List<string> failed)
    {
        if (Hotkey.TryParse(text) is not { } hk) return;
        if (_window.RegisterHotkey(id, hk.ToWin32Modifiers(), hk.ToVirtualKey()))
            _registered.Add(id);
        else
            failed.Add(hk.ToString());
    }

    private void OnHotkeyPressed(int id)
    {
        switch (id)
        {
            case LayoutMenuId: LayoutMenuRequested?.Invoke(); break;
            case > FirstSlotId and <= FirstSlotId + 9: SlotRequested?.Invoke(id - FirstSlotId); break;
        }
    }

    public void Dispose() => _window.Dispose();
}
