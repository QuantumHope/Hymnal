using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using Hymnal.Core.Models;
using ReactiveUI;
using Unit = System.Reactive.Unit;

namespace Hymnal.ViewModels;

public sealed class ProjectTypeDialogViewModel : ViewModelBase
{
    public ProjectTypeDialogViewModel()
    {
        Cards = ProjectTypeCatalog.All.Select(i => new ProjectTypeCardViewModel(i)).ToList();

        SelectCommand = ReactiveCommand.Create<ProjectTypeCardViewModel, Unit>(card =>
        {
            foreach (var c in Cards)
                c.IsSelected = ReferenceEquals(c, card);
            SelectedKind = card.Info.Kind;
            return Unit.Default;
        });

        var canCreate = this.WhenAnyValue(x => x.SelectedKind).Select(k => k.HasValue);
        CreateCommand = ReactiveCommand.Create<Unit, ProjectKind>(_ => SelectedKind!.Value, canCreate);
    }

    public IReadOnlyList<ProjectTypeCardViewModel> Cards { get; }

    private ProjectKind? _selectedKind;
    public ProjectKind? SelectedKind
    {
        get => _selectedKind;
        private set => this.RaiseAndSetIfChanged(ref _selectedKind, value);
    }

    public ReactiveCommand<ProjectTypeCardViewModel, Unit> SelectCommand { get; }
    public ReactiveCommand<Unit, ProjectKind> CreateCommand { get; }
}
