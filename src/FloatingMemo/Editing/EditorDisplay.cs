namespace FloatingMemo.Editing;

/// <summary>Display options of the editor that the settings window can change while a memo is open.</summary>
public static class EditorDisplay
{
    public static event EventHandler<bool>? WrapChanged;

    public static void SetWrap(bool wrap) => WrapChanged?.Invoke(null, wrap);
}
