using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DD2ModManager.Core;
using Microsoft.Win32;

namespace DD2ModManager.Services;

public static class Dialogs
{
    /// <summary>False while rendering screenshots: every question answers "no" and errors only go to the log.</summary>
    public static bool Interactive { get; set; } = true;

    private static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

    public static bool Confirm(string title, string message)
    {
        if (!Interactive) return false;
        return MessageBox.Show(Owner!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    public static void Info(string title, string message)
    {
        if (Interactive) MessageBox.Show(Owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public static void Warn(string title, string message)
    {
        if (Interactive) MessageBox.Show(Owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public static void Error(string message)
    {
        Log.Error(message);
        if (Interactive) MessageBox.Show(Owner!, message, "DD2 Mod Manager", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

public static class ShellOps
{
    public static void Open(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    public static void LaunchGame(int steamAppId) => Open($"steam://rungameid/{steamAppId}");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct op);

    /// <summary>Moves a file to the Recycle Bin so a replaced archive can still be recovered.</summary>
    public static bool Recycle(string path)
    {
        const uint FO_DELETE = 3;
        const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;
        var op = new ShFileOpStruct
        {
            wFunc = FO_DELETE,
            pFrom = Path.GetFullPath(path) + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        return SHFileOperation(ref op) == 0 && op.fAnyOperationsAborted == 0 && !File.Exists(path) && !Directory.Exists(path);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void UseDarkTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }
}

/// <summary>Registers this app for nxm:// links (Nexus's "Mod Manager Download" button) for the current user.</summary>
public static class NxmProtocol
{
    private const string ClassKey = @"Software\Classes\nxm";

    public static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path");

    public static string? CurrentCommand()
    {
        using var user = Registry.CurrentUser.OpenSubKey(ClassKey + @"\shell\open\command");
        if (user?.GetValue("") is string command) return command;
        using var root = Registry.ClassesRoot.OpenSubKey(@"nxm\shell\open\command");
        return root?.GetValue("") as string;
    }

    public static bool IsOurs => CurrentCommand()?.Contains(ExePath, StringComparison.OrdinalIgnoreCase) == true;

    public static string Describe()
    {
        var command = CurrentCommand();
        if (command == null) return "Nothing is handling Nexus download links yet.";
        if (IsOurs) return "Nexus \"Mod Manager Download\" links open in this app.";
        var exe = command.Trim().StartsWith('"') ? command.Trim().Split('"')[1] : command.Split(' ')[0];
        return $"Nexus download links currently open in {Path.GetFileName(exe)}.";
    }

    /// <summary>Points nxm:// at this exe. Returns the previous command so it can be restored.</summary>
    public static string? Register()
    {
        var previous = IsOurs ? null : CurrentCommand();
        using var key = Registry.CurrentUser.CreateSubKey(ClassKey);
        key.SetValue("", "URL:NXM Protocol");
        key.SetValue("URL Protocol", "");
        using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{ExePath}\",0");
        using (var command = key.CreateSubKey(@"shell\open\command")) command.SetValue("", $"\"{ExePath}\" \"%1\"");
        Log.Info("Registered as the nxm:// handler");
        return previous;
    }

    public static void Restore(string previousCommand)
    {
        using var command = Registry.CurrentUser.CreateSubKey(ClassKey + @"\shell\open\command");
        command.SetValue("", previousCommand);
        Log.Info("Restored the previous nxm:// handler");
    }
}

/// <summary>Keeps one running copy. Later launches (e.g. from an nxm:// click) hand their argument to it over a named pipe.</summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly string PipeName = "DD2ModManager-" + Environment.UserName;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();

    public bool IsFirst { get; }

    public SingleInstance()
    {
        _mutex = new Mutex(true, @"Local\DD2ModManager.Instance", out var created);
        IsFirst = created;
    }

    public static bool Send(string message)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(message);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            return false;
        }
    }

    public void Listen(Action<string> onMessage) => Task.Run(async () =>
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(server);
                if (await reader.ReadLineAsync(_stop.Token) is { } line) onMessage(line);
            }
            catch (OperationCanceledException) { return; }
            catch (IOException ex) { Log.Warn($"Instance pipe: {ex.Message}"); }
        }
    });

    public void Dispose()
    {
        _stop.Cancel();
        if (IsFirst) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
