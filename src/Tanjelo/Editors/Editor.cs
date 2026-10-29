using System.Windows;
using System.Windows.Input;
using Tanjelo.Core;

namespace Tanjelo.Editors;

/// <summary>
/// One editor in the <see cref="EditorWindow"/>: what it shows, its tools, and the tool entry it fills in when saved,
/// which the engine then runs like any other tool. The window brings the frame, undo and redo, zoom and Save.
/// </summary>
abstract class Editor
{
    /// <summary>The editor for a tool entry that names one (<see cref="Target.Editor"/>).</summary>
    public static Editor For(ConverterEngine engine, string file, Target target) => target.Editor switch
    {
        EditorKind.OrganizePdf => new PageEditor(file, target),
        EditorKind.Redact => new RedactEditor(file, target),
        EditorKind.Annotate => new AnnotateEditor(file, target),
        EditorKind.Photo => new PhotoEditor(file, target),
        EditorKind.Trim => new TrimEditor(file, target, engine.Tools),
        EditorKind.Bleep => new BleepEditor(file, target, engine.Tools),
        EditorKind.RedactVideo => new VideoRedactEditor(file, target, engine.Tools),
        _ => throw new NotSupportedException($"There's no editor for {target.Key} yet."),
    };

    /// <summary>The canvas or grid in the middle of the window.</summary>
    public abstract FrameworkElement Main { get; }

    /// <summary>Buttons above the canvas, on the left; undo and redo are on the right.</summary>
    public virtual FrameworkElement? Toolbar => null;

    /// <summary>Settings beside the canvas.</summary>
    public virtual FrameworkElement? Panel => null;

    /// <summary>The zoomable picture, when the editor has one; the window adds the zoom controls for it.</summary>
    public virtual ZoomCanvas? Canvas => null;

    /// <summary>What the editor works on, such as "12 pages", shown under the file name.</summary>
    public virtual string Summary => "";

    /// <summary>Something to know before saving, such as what won't survive, shown over the canvas; null for nothing.</summary>
    public virtual string? Notice => null;

    /// <summary>A line about the current state for the bottom bar, such as "3 selected".</summary>
    public virtual string Status => "";

    public abstract bool CanUndo { get; }
    public abstract bool CanRedo { get; }

    /// <summary>Whether anything differs from the original; Cancel only asks when it does, and Save needs it.</summary>
    public abstract bool Changed { get; }

    public abstract void Undo();
    public abstract void Redo();

    /// <summary>Reads the file. Problems worth showing come as <see cref="ConversionException"/>.</summary>
    public abstract Task LoadAsync(CancellationToken cancel);

    /// <summary>The tool entry with the edits as its parameters, for the engine to save beside the original.</summary>
    public abstract Target Result();

    /// <summary>A key pressed while the window is active; true when the editor used it.</summary>
    public virtual bool OnKey(KeyEventArgs e) => Press(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers, e.IsRepeat);

    /// <summary>A key pressed with these modifiers, as <see cref="OnKey"/> passes it on; true when used. Tests press keys here.</summary>
    public virtual bool Press(Key key, ModifierKeys modifiers = ModifierKeys.None, bool repeat = false) => false;

    /// <summary>What a screen reader calls the canvas or timeline, and the keys it takes, for its help text.</summary>
    public virtual (string Name, string Keys) Spoken => (Strings.Picture, "");

    /// <summary>The window closed: let go of players and delete temporary files.</summary>
    public virtual void Close() { }

    /// <summary>A key let go, such as the one held to see the original.</summary>
    public virtual bool OnKeyUp(KeyEventArgs e) => false;

    /// <summary>The state, the undo steps or the status changed.</summary>
    public event Action? Updated;

    protected void RaiseUpdated() => Updated?.Invoke();
}

/// <summary>An editor whose state is a snapshot of type <typeparamref name="T"/>, with undo and redo between snapshots.</summary>
abstract class Editor<T> : Editor
{
    UndoStack<T>? _history;

    protected UndoStack<T> History => _history ?? throw new InvalidOperationException("The editor hasn't loaded yet.");

    protected T State => History.Current;

    /// <summary>Whether the editor has loaded and has a state.</summary>
    protected bool Started => _history is not null;

    public override bool CanUndo => _history?.CanUndo == true;
    public override bool CanRedo => _history?.CanRedo == true;
    public override bool Changed => _history?.Changed == true;

    /// <summary>Starts the history from the state as loaded.</summary>
    protected void Begin(T initial)
    {
        _history = new UndoStack<T>(initial);
        Show(initial);
        RaiseUpdated();
    }

    /// <summary>A new state, as one undo step.</summary>
    protected void Commit(T state)
    {
        History.Push(state);
        Show(state);
        RaiseUpdated();
    }

    /// <summary>Changes the state from a gesture under way, after it committed once as it started.</summary>
    protected void Amend(T state)
    {
        History.Amend(state);
        Show(state);
        RaiseUpdated();
    }

    /// <summary>Takes back the step a gesture started, when it came to nothing.</summary>
    protected void Retract()
    {
        Show(History.Discard());
        RaiseUpdated();
    }

    public override void Undo()
    {
        if (!CanUndo) return;
        Show(History.Undo());
        RaiseUpdated();
    }

    public override void Redo()
    {
        if (!CanRedo) return;
        Show(History.Redo());
        RaiseUpdated();
    }

    /// <summary>Redraws everything for <paramref name="state"/>.</summary>
    protected abstract void Show(T state);
}
