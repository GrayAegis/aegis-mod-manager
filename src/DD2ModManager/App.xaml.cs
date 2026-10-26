using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DD2ModManager.Core;
using DD2ModManager.Core.State;
using DD2ModManager.Services;
using DD2ModManager.ViewModels;

namespace DD2ModManager;

public partial class App : Application
{
    private SingleInstance? _instance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        var nxm = args.FirstOrDefault(a => a.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase));
        var screenshot = ArgAfter(args, "--screenshot");

        if (screenshot != null)
        {
            // Render-only mode for checking the UI: works on a copy of the real data, never prompts, never uses the
            // Nexus account, never changes the game folder.
            var real = AppPaths.DataDir;
            AppPaths.DataDir = Path.Combine(Path.GetTempPath(), "DD2ModManager-screenshot", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(AppPaths.DataDir);
            foreach (var file in new[] { "settings.json", "state.json", "nexus-cache.json" })
                if (File.Exists(Path.Combine(real, file))) File.Copy(Path.Combine(real, file), Path.Combine(AppPaths.DataDir, file));
            Dialogs.Interactive = false;
        }
        else
        {
            _instance = new SingleInstance();
            if (!_instance.IsFirst)
            {
                SingleInstance.Send(nxm ?? "activate");
                Shutdown();
                return;
            }
        }

        DispatcherUnhandledException += OnUnhandled;
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        window.Show();
        _instance?.Listen(message => Dispatcher.InvokeAsync(() => vm.HandleExternalMessageAsync(message)));

        await vm.InitializeAsync(nxm);

        if (screenshot != null)
        {
            if (ArgAfter(args, "--select") is { } select)
                vm.Selected = vm.Items.FirstOrDefault(i => i.Name.Contains(select, StringComparison.OrdinalIgnoreCase));
            if (ArgAfter(args, "--filter") is { } filter) vm.Filter = filter;
            if (vm.Selected != null) await vm.Selected.LoadScreenshotAsync();
            await Task.Delay(400);
            await Dispatcher.InvokeAsync(() => Render(window, screenshot), DispatcherPriority.ApplicationIdle);
            if (ArgAfter(args, "--settings-shot") is { } settingsShot)
            {
                var settings = new SettingsWindow { DataContext = new SettingsViewModel(vm.Manager), ShowActivated = false };
                settings.Show();
                await Dispatcher.InvokeAsync(() => Render(settings, settingsShot), DispatcherPriority.ApplicationIdle);
                settings.Close();
            }
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error($"Unhandled: {e.Exception}");
        if (Dialogs.Interactive) MessageBox.Show(e.Exception.Message, "DD2 Mod Manager", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static string? ArgAfter(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static void Render(Window window, string path)
    {
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
