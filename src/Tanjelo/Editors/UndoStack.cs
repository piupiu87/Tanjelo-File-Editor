namespace Tanjelo.Editors;

/// <summary>
/// The states an editor has been through, for undo and redo. States are whole snapshots that are never changed once
/// pushed, such as a page list or a list of marks. A gesture that changes things continuously, like dragging a
/// slider, pushes once as it starts and then <see cref="Amend"/>s that state.
/// </summary>
sealed class UndoStack<T>
{
    /// <summary>How many steps back undo can go; older ones are let go.</summary>
    public const int Depth = 200;

    readonly List<T> _states;
    int _at;
    int _dropped;

    public UndoStack(T initial) => _states = [initial];

    public T Current => _states[_at];

    public bool CanUndo => _at > 0;
    public bool CanRedo => _at < _states.Count - 1;

    /// <summary>Whether the current state differs from the first one by any steps; undoing them all goes back to unchanged.</summary>
    public bool Changed => _at + _dropped > 0;

    /// <summary>A new state after the current one. Anything that could have been redone is gone.</summary>
    public void Push(T state)
    {
        _states.RemoveRange(_at + 1, _states.Count - _at - 1);
        _states.Add(state);
        _at++;
        if (_states.Count > Depth + 1)
        {
            _states.RemoveAt(0);
            _at--;
            _dropped++;
        }
    }

    /// <summary>Replaces the current state, as a gesture under way changes it further, without a new undo step.</summary>
    public void Amend(T state)
    {
        _states[_at] = state;
        _states.RemoveRange(_at + 1, _states.Count - _at - 1);
    }

    public T Undo()
    {
        if (CanUndo) _at--;
        return Current;
    }

    public T Redo()
    {
        if (CanRedo) _at++;
        return Current;
    }
}
