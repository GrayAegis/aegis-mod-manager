using System.Windows;
using DD2ModManager.Services;
using DD2ModManager.ViewModels;

namespace DD2ModManager;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ShellOps.UseDarkTitleBar(this);
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is SettingsViewModel vm) vm.CloseRequested += ok => DialogResult = ok;
        };
    }

    private void OnKeyChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.NewApiKey = KeyBox.Password;
    }
}
