using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace NightlyDawn.App.Timelines;

/// <summary>
/// One tab: a titled set of timeline columns plus which one is selected (plan §11.1). Columns are built by
/// <paramref name="columnFactory"/> passed in at construction -- the same delegate <see cref="WorkspaceViewModel"/>
/// was given, so every column in the workspace (not just this tab's) comes from the one factory the host built
/// in <c>MainWindow</c> (plan §11.1: "列の生成は1箇所に閉じる").
/// </summary>
public sealed class TabViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Func<TimelineColumnViewModel> _columnFactory;
    private string _title;
    private TimelineColumnViewModel? _selectedColumn;
    private bool _disposed;

    public TabViewModel(string title, Func<TimelineColumnViewModel> columnFactory)
    {
        ArgumentNullException.ThrowIfNull(columnFactory);
        _title = title ?? string.Empty;
        _columnFactory = columnFactory;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value ?? string.Empty);
    }

    /// <summary>This tab's columns, left-to-right (plan §11.5). Can reach zero after <see cref="RemoveColumn"/>
    /// removes the last one -- nothing in this class stops that; <see cref="WorkspaceViewModel.ActiveSink"/>
    /// is what turns an empty tab into a null compose target.</summary>
    public ObservableCollection<TimelineColumnViewModel> Columns { get; } = [];

    /// <summary>Null when <see cref="Columns"/> is empty.</summary>
    public TimelineColumnViewModel? SelectedColumn
    {
        get => _selectedColumn;
        set => SetField(ref _selectedColumn, value);
    }

    /// <summary>Builds a new column through the factory, appends it, and selects it.</summary>
    public TimelineColumnViewModel AddColumn()
    {
        var column = _columnFactory();
        Columns.Add(column);
        SelectedColumn = column;
        return column;
    }

    /// <summary>Removes <paramref name="column"/> from this tab and disposes it (plan §11.3: closing a column
    /// must stop its relay subscription, not just drop the UI's reference to it). A no-op if the column is not
    /// (or no longer) in this tab.</summary>
    public void RemoveColumn(TimelineColumnViewModel column)
    {
        if (!Columns.Remove(column))
        {
            return;
        }

        column.Dispose();

        if (ReferenceEquals(SelectedColumn, column))
        {
            SelectedColumn = Columns.FirstOrDefault();
        }
    }

    /// <summary>Disposes every column (plan §11.3: closing this tab closes everything under it).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var column in Columns)
        {
            column.Dispose();
        }
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
