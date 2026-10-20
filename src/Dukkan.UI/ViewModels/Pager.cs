using System.Windows.Input;

namespace Dukkan.UI;

public sealed class PageNumber(int number, bool isCurrent)
{
    public int Number { get; } = number;
    public bool IsCurrent { get; } = isCurrent;
}

/// <summary>Client-side paging over an in-memory list. Bind a ui:PaginationBar to it.</summary>
public sealed class Pager<T> : ObservableObject
{
    IReadOnlyList<T> _source = [];
    int _pageIndex = 1;
    IReadOnlyList<T> _items = [];

    public Pager(int pageSize)
    {
        PageSize = pageSize;
        PreviousCommand = new RelayCommand(() => GoTo(_pageIndex - 1), () => _pageIndex > 1);
        NextCommand = new RelayCommand(() => GoTo(_pageIndex + 1), () => _pageIndex < TotalPages);
        GoToCommand = new RelayCommand(p => GoTo((int)p!));
        Localizer.Instance.PropertyChanged += (_, _) => Raise(nameof(ShowingText));
    }

    public int PageSize { get; }
    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand GoToCommand { get; }
    public int TotalCount => _source.Count;
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(_source.Count / (double)PageSize));
    public IReadOnlyList<T> Items { get => _items; private set => Set(ref _items, value); }
    public bool HasPages => TotalPages > 1;
    public IReadOnlyList<PageNumber> PageNumbers => Enumerable.Range(1, TotalPages).Select(n => new PageNumber(n, n == _pageIndex)).ToList();

    public string ShowingText
    {
        get
        {
            if (_source.Count == 0) return "";
            var from = (_pageIndex - 1) * PageSize + 1;
            var to = Math.Min(_source.Count, _pageIndex * PageSize);
            return string.Format(Localizer.Instance["Pager_Showing"], from, to, _source.Count);
        }
    }

    public void SetSource(IReadOnlyList<T> source, bool resetPage = true)
    {
        _source = source;
        if (resetPage) _pageIndex = 1;
        _pageIndex = Math.Min(_pageIndex, TotalPages);
        Refresh();
    }

    void GoTo(int page)
    {
        _pageIndex = Math.Clamp(page, 1, TotalPages);
        Refresh();
    }

    void Refresh()
    {
        Items = _source.Skip((_pageIndex - 1) * PageSize).Take(PageSize).ToList();
        Raise(nameof(TotalCount)); Raise(nameof(TotalPages)); Raise(nameof(PageNumbers)); Raise(nameof(ShowingText)); Raise(nameof(HasPages));
        CommandManager.InvalidateRequerySuggested();
    }
}
