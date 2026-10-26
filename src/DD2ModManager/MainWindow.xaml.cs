using System.Windows;
using DD2ModManager.Services;
using DD2ModManager.ViewModels;

namespace DD2ModManager;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ShellOps.UseDarkTitleBar(this);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && DataContext is MainViewModel vm)
            await vm.AddFilesAsync(files);
    }
}
