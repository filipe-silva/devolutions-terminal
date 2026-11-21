using System.Globalization;
using Avalonia;
using Avalonia.Input.TextInput;

namespace Devolutions.Terminal;

internal sealed class TerminalTextInputMethodClient(TermControl owner) : TextInputMethodClient
{
    private string _preeditText = string.Empty;
    private int? _preeditCursor;

    // What the keyboard typed since the last other input, when the keyboard edits text instead of composing it
    // (Android: a suggestion or a correction selects the old word and deletes it, then types the new one).
    // The screen cannot stand in for it: the program's echo comes later, and what a program draws is not text
    // the keyboard may take back. The terminal's cursor stays at its end.
    private string _typed = string.Empty;
    private TextSelection _selection;
    private bool _deletePending;

    /// <summary>Whether the keyboard edits the text it typed (Android's) rather than composing it (a desktop IME's).</summary>
    internal bool TracksTyping { get; set; } = OperatingSystem.IsAndroid();

    public override Visual TextViewVisual => owner;

    public override bool SupportsPreedit => true;

    public override bool SupportsSurroundingText => true;

    public override string SurroundingText => TracksTyping ? _typed : owner.GetImeContext().Text;

    public override Rect CursorRectangle => owner.GetImeCursorRectangle();

    public override TextSelection Selection
    {
        get
        {
            if (TracksTyping)
            {
                return _selection;
            }

            var offset = owner.GetImeContext().CursorTextOffset;
            return new TextSelection(offset, offset);
        }
        set
        {
            if (TracksTyping)
            {
                FlushDelete();
                var start = Math.Clamp(Math.Min(value.Start, value.End), 0, _typed.Length);
                var end = Math.Clamp(Math.Max(value.Start, value.End), 0, _typed.Length);
                _selection = new TextSelection(start, end);
            }
            else
            {
                var offset = Math.Clamp(value.End, 0, SurroundingText.Length);
                owner.SetImeSelectionOffset(offset);
            }

            RaiseSelectionChanged();
            RaiseCursorRectangleChanged();
        }
    }

    public override void SetPreeditText(string? preeditText, int? cursorPos)
    {
        _preeditText = preeditText ?? string.Empty;
        _preeditCursor = cursorPos;
        owner.SetImeComposition(_preeditText, _preeditCursor);
        RaiseSurroundingTextChanged();
        RaiseCursorRectangleChanged();
    }

    internal void NotifyCursorChanged()
    {
        if (TracksTyping)
        {
            RaiseCursorRectangleChanged();
            return;
        }

        RaiseSurroundingTextChanged();
        RaiseSelectionChanged();
        RaiseCursorRectangleChanged();
    }

    /// <summary>
    /// Text the keyboard types, in place of what it selected: the terminal gets backspaces back to the edit,
    /// then the new text and whatever followed the edit, typed again. False when nothing is tracked.
    /// </summary>
    internal bool Typing(string text)
    {
        if (!TracksTyping)
        {
            return false;
        }

        // A delete just before (a suggestion replacing a word) is part of this edit: the selection still
        // names what it deletes, and what the new text starts with the same need not go and come back.
        _deletePending = false;
        Send(Edit(_selection.Start, _selection.End, text));
        return true;
    }

    /// <summary>
    /// The keyboard deletes what it selected (a forward delete over a selection). Held until the text that
    /// usually follows; on its own, sent once the keyboard is done. False when nothing is selected.
    /// </summary>
    internal bool DeletingSelection()
    {
        if (!TracksTyping || _selection.Start == _selection.End)
        {
            return false;
        }

        _deletePending = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                if (_deletePending)
                {
                    FlushDelete();
                    Changed();
                }
            },
            Avalonia.Threading.DispatcherPriority.Input);
        return true;
    }

    internal void FlushDelete()
    {
        if (!_deletePending)
        {
            return;
        }

        _deletePending = false;
        Send(Edit(_selection.Start, _selection.End, string.Empty));
    }

    private void Send((int Backspaces, string Text) edit) => owner.SendImeEdit(edit.Backspaces, edit.Text);

    /// <summary>A backspace typed at the end: the last character goes from what is tracked too.</summary>
    internal void Backspaced()
    {
        FlushDelete();
        if (!TracksTyping || _typed.Length == 0 || _selection.Start != _typed.Length)
        {
            Forget();
            return;
        }

        var elements = StringInfo.ParseCombiningCharacters(_typed);
        _typed = _typed[..elements[^1]];
        _selection = new TextSelection(_typed.Length, _typed.Length);
        Changed();
    }

    /// <summary>Other input (a key, a tap, focus): the keyboard starts again from nothing.</summary>
    internal void Forget()
    {
        FlushDelete();
        if (!TracksTyping || (_typed.Length == 0 && _selection.Start == 0 && _selection.End == 0))
        {
            return;
        }

        _typed = string.Empty;
        _selection = default;
        Changed();
    }

    private (int Backspaces, string Text) Edit(int start, int end, string text)
    {
        // Keep what the edit leaves as it was: only the part after it goes and comes back.
        var removed = _typed[start..end];
        var same = 0;
        while (same < removed.Length && same < text.Length && removed[same] == text[same])
        {
            same++;
        }

        while (same > 0 && char.IsHighSurrogate(text[same - 1]))
        {
            same--;
        }

        var tail = _typed[end..];
        var from = start + same;
        var backspaces = new StringInfo(_typed[from..]).LengthInTextElements;
        _typed = _typed[..start] + text + tail;
        var caret = start + text.Length;
        _selection = new TextSelection(caret, caret);
        if (backspaces == 0 && tail.Length == 0)
        {
            return (0, text[same..]);
        }

        return (backspaces, text[same..] + tail);
    }

    private void Changed()
    {
        RaiseSurroundingTextChanged();
        RaiseSelectionChanged();
        RaiseCursorRectangleChanged();
    }
}
