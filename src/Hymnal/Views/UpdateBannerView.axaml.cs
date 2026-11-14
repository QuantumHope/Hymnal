using Avalonia.Controls;

namespace Hymnal.Views;

/// <summary>The update banner at the top of the main window; its DataContext is the app's UpdatesViewModel.</summary>
public partial class UpdateBannerView : UserControl
{
    public UpdateBannerView()
    {
        InitializeComponent();
    }
}
