using ReactiveUI;

namespace Hymnal.ViewModels;

public sealed class ProjectTypeCardViewModel : ViewModelBase
{
    public ProjectTypeCardViewModel(ProjectTypeInfo info)
    {
        Info = info;
    }

    public ProjectTypeInfo Info { get; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
}
