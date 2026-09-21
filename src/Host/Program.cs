using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace GestureCompanionPointerHost;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Forms.Application.EnableVisualStyles();
        Forms.Application.SetCompatibleTextRenderingDefault(false);
        _ = new App { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        Forms.Application.Run(new TrayAppContext(HostOptions.Parse(args)));
    }
}

static class BridgeLog
{
    private static readonly object Gate = new();
    public static readonly string PathName = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GestureCompanion", "bridge.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var directory = Path.GetDirectoryName(PathName);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.AppendAllText(PathName, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { }
    }
}
sealed class TrayAppContext : Forms.ApplicationContext
{
    private const string StartupValueName = "Gesture Companion Pointer Host";
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Forms.ToolStripMenuItem _startupItem;
    private readonly HostOptions _options;
    private readonly BridgeManager _bridgeManager;
    private readonly Forms.Timer _monitorTimer;
    private PointerHostSettings _settings;
    private SettingsWindow? _settingsWindow;
    private int _monitorBusy;

    public TrayAppContext(HostOptions options)
    {
        _options = options;
        _settings = PointerHostSettings.Load();
        _bridgeManager = new BridgeManager(options);

        _startupItem = new Forms.ToolStripMenuItem("Start with Windows") { Checked = IsStartupEnabled() };
        _startupItem.Click += (_, _) => ToggleStartup();

        var settingsItem = new Forms.ToolStripMenuItem("Settings");
        settingsItem.Click += (_, _) => ShowSettings();

        var quitItem = new Forms.ToolStripMenuItem("Quit");
        quitItem.Click += (_, _) => ExitThread();

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "Gesture Companion",
            Icon = LoadTrayIcon(),
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };
        _trayIcon.ContextMenuStrip.Items.Add(settingsItem);
        _trayIcon.ContextMenuStrip.Items.Add(_startupItem);
        _trayIcon.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
        _trayIcon.ContextMenuStrip.Items.Add(quitItem);
        _trayIcon.DoubleClick += (_, _) => ShowSettings();
        _bridgeManager.FailureWarning += message =>
        {
            try
            {
                _trayIcon.ShowBalloonTip(5000, "Gesture Companion bridge", message, Forms.ToolTipIcon.Warning);
            }
            catch { }
        };

        _monitorTimer = new Forms.Timer { Interval = 500 };
        _monitorTimer.Tick += (_, _) => MonitorForeground();
        _monitorTimer.Start();
        MonitorForeground();
    }

    private void MonitorForeground()
    {
        if (Interlocked.Exchange(ref _monitorBusy, 1) != 0)
        {
            return;
        }

        var settings = _settings.Clone();
        _ = Task.Run(() =>
        {
            try
            {
                _bridgeManager.EnsureForSupportedForeground(settings);
            }
            finally
            {
                Interlocked.Exchange(ref _monitorBusy, 0);
            }
        });
    }

    private void ShowSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        var window = new SettingsWindow(_settings);
        _settingsWindow = window;
        try
        {
            if (window.ShowDialog() == true)
            {
                _settings = window.ResultSettings.Clone();
                _settings.Save();
                _bridgeManager.InvalidateConfiguration();
                MonitorForeground();
            }
        }
        finally
        {
            if (ReferenceEquals(_settingsWindow, window))
            {
                _settingsWindow = null;
            }
        }
    }

    private static Icon LoadTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "icon.ico");
        return File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application;
    }

    private void ToggleStartup()
    {
        SetStartupEnabled(!_startupItem.Checked);
        _startupItem.Checked = IsStartupEnabled();
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
        return key?.GetValue(StartupValueName) is string value &&
               value.Contains(Forms.Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    private static void SetStartupEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (key is null)
        {
            return;
        }

        if (enabled)
        {
            key.SetValue(StartupValueName, '"' + Forms.Application.ExecutablePath + '"');
        }
        else
        {
            key.DeleteValue(StartupValueName, false);
        }
    }

    protected override void ExitThreadCore()
    {
        _monitorTimer.Stop();
        _monitorTimer.Dispose();
        _bridgeManager.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        System.Windows.Application.Current?.Shutdown();
        base.ExitThreadCore();
    }
}
sealed record HostOptions(string BridgeRoot)
{
    public static HostOptions Parse(string[] args)
    {
        var packagedBridgeRoot = Path.Combine(AppContext.BaseDirectory, "Bridge");
        var bridgeRoot = Directory.Exists(packagedBridgeRoot)
            ? packagedBridgeRoot
            : FindDevelopmentRoot();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--bridge-root" && i + 1 < args.Length)
            {
                bridgeRoot = args[++i];
            }
        }
        return new HostOptions(bridgeRoot);
    }

    private static string FindDevelopmentRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; directory is not null && depth < 10; depth++, directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "Injector")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src", "NativePayload")))
            {
                return directory.FullName;
            }
        }
        return AppContext.BaseDirectory;
    }
}

sealed class BridgeManager : IDisposable
{
    private readonly HostOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<int, ManagedBridge> _bridges = new();
    private readonly Dictionary<int, DateTime> _lastStartAttempts = new();
    private readonly Dictionary<int, int> _consecutiveFailures = new();

    public event Action<string>? FailureWarning;

    public BridgeManager(HostOptions options) => _options = options;

    public void EnsureForSupportedForeground(PointerHostSettings settings)
    {
        var foreground = ForegroundTarget.TryGet();

        lock (_gate)
        {
            RemoveExitedBridges();
            if (foreground is null)
            {
                return;
            }

            if (!_bridges.TryGetValue(foreground.ProcessId, out var managedBridge))
            {
                if (_lastStartAttempts.TryGetValue(foreground.ProcessId, out var lastAttempt) &&
                    DateTime.UtcNow - lastAttempt < TimeSpan.FromSeconds(2))
                {
                    return;
                }

                _lastStartAttempts[foreground.ProcessId] = DateTime.UtcNow;
                var spec = BridgeLaunchSpec.ForProcess(_options.BridgeRoot, foreground);
                if (spec is null)
                {
                    RecordFailure(foreground, "Injector or payload file is missing.");
                    return;
                }

                try
                {
                    var bridge = ControlBridge.StartAsync(spec).GetAwaiter().GetResult();
                    managedBridge = new ManagedBridge(bridge);
                    _bridges.Add(foreground.ProcessId, managedBridge);
                }
                catch (Exception exception)
                {
                    RecordFailure(foreground, $"Bridge startup failed: {exception.GetType().Name}: {exception.Message}");
                    return;
                }

                _consecutiveFailures.Remove(foreground.ProcessId);
                BridgeLog.Write($"Bridge ready for {foreground.ProcessName}.exe pid={foreground.ProcessId}.");
            }

            if (DateTime.UtcNow - managedBridge.LastHealthCheck >= TimeSpan.FromSeconds(2))
            {
                managedBridge.LastHealthCheck = DateTime.UtcNow;
                var snapshot = managedBridge.Bridge.Send("debug");
                if (snapshot is null)
                {
                    RecordFailure(foreground, "Bridge control pipe stopped responding.");
                    RemoveBridge(foreground.ProcessId);
                    return;
                }
                managedBridge.LastDebugSnapshot = snapshot;
                if (DateTime.UtcNow - managedBridge.LastSnapshotLog >= TimeSpan.FromSeconds(10))
                {
                    BridgeLog.Write($"{foreground.ProcessName}.exe pid={foreground.ProcessId} snapshot: {snapshot}");
                    managedBridge.LastSnapshotLog = DateTime.UtcNow;
                }
            }

            var signature = BuildSignature(settings);
            if (signature == managedBridge.ConfiguredSignature)
            {
                return;
            }

            ConfigureBridge(managedBridge.Bridge, settings);
            if (!managedBridge.Bridge.IsHealthy)
            {
                RemoveBridge(foreground.ProcessId);
                return;
            }

            managedBridge.ConfiguredSignature = signature;
        }
    }

    public string? GetDebugSnapshot()
    {
        var foreground = ForegroundTarget.TryGet();
        lock (_gate)
        {
            RemoveExitedBridges();
            if (foreground is null || !_bridges.TryGetValue(foreground.ProcessId, out var managedBridge))
            {
                return null;
            }

            return managedBridge.Bridge.Send("debug");
        }
    }

    public void InvalidateConfiguration()
    {
        lock (_gate)
        {
            foreach (var managedBridge in _bridges.Values)
            {
                managedBridge.ConfiguredSignature = null;
            }
        }
    }

    private static void ConfigureBridge(ControlBridge bridge, PointerHostSettings settings)
    {
        ConfigureBinding(bridge, "pinch-open", settings.PinchOpenShortcut);
        ConfigureBinding(bridge, "pinch-close", settings.PinchCloseShortcut);
        ConfigureBinding(bridge, "rotate-cw", settings.RotateClockwiseShortcut);
        ConfigureBinding(bridge, "rotate-ccw", settings.RotateCounterClockwiseShortcut);
        ConfigureBinding(bridge, "pan", settings.PanDragShortcut);
        ConfigureBinding(bridge, "one-finger-slide", settings.OneFingerSlideShortcut);
        bridge.Send($"mode one-finger-slide-pan {(settings.OneFingerSlideUsesPan ? "on" : "off")}");
        ConfigureBinding(bridge, "one-finger-hold", settings.OneFingerHoldShortcut);
        bridge.Send($"mode one-finger-hold-right-click {(settings.OneFingerHoldUsesRightClick ? "on" : "off")}");

        ConfigureBinding(bridge, "undo", settings.TwoFingerTapShortcut);
        ConfigureBinding(bridge, "redo-three", settings.ThreeFingerTapShortcut);
        ConfigureBinding(bridge, "redo-four", settings.FourFingerTapShortcut);


        bridge.Send($"step zoom {settings.ZoomStepPixels.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        bridge.Send($"step rotate {settings.RotateStepDegrees.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        bridge.Send($"step pan {settings.PanStepPixels.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        bridge.Send($"interval zoom {settings.ZoomIntervalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        bridge.Send($"interval rotate {settings.RotateIntervalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    }

    private static void ConfigureBinding(ControlBridge bridge, string gesture, string shortcut)
    {
        var value = string.IsNullOrWhiteSpace(shortcut) ? "disabled" : shortcut.Trim();
        bridge.Send($"bind {gesture} {value}");
    }

    private void RemoveExitedBridges()
    {
        foreach (var processId in _bridges
                     .Where(pair => !pair.Value.Bridge.IsHealthy)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            RemoveBridge(processId);
        }
    }

    private void RemoveBridge(int processId)
    {
        if (_bridges.Remove(processId, out var managedBridge))
        {
            managedBridge.Bridge.Dispose();
        }
        _lastStartAttempts.Remove(processId);
    }

    private void RecordFailure(ForegroundTarget target, string detail)
    {
        BridgeLog.Write($"{target.ProcessName}.exe pid={target.ProcessId}: {detail}");
        var count = _consecutiveFailures.TryGetValue(target.ProcessId, out var previous) ? previous + 1 : 1;
        _consecutiveFailures[target.ProcessId] = count;
        if (count == 3)
        {
            FailureWarning?.Invoke($"Could not connect to {target.ProcessName}. Retrying in the background. See bridge.log for details.");
        }
    }
    private static string BuildSignature(PointerHostSettings settings) => string.Join(
        "\n",
        settings.PinchOpenShortcut,
        settings.PinchCloseShortcut,
        settings.RotateClockwiseShortcut,
        settings.RotateCounterClockwiseShortcut,
        settings.PanDragShortcut,
        settings.OneFingerSlideShortcut,
        settings.OneFingerSlideUsesPan,
        settings.OneFingerHoldShortcut,
        settings.OneFingerHoldUsesRightClick,

        settings.TwoFingerTapShortcut,
        settings.ThreeFingerTapShortcut,
        settings.FourFingerTapShortcut,

        settings.ZoomStepPixels,
        settings.RotateStepDegrees,
        settings.PanStepPixels,
        settings.ZoomIntervalMilliseconds,
        settings.RotateIntervalMilliseconds);

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var managedBridge in _bridges.Values)
            {
                managedBridge.Bridge.Dispose();
            }

            _bridges.Clear();
            _lastStartAttempts.Clear();
        }
    }

    private sealed class ManagedBridge(ControlBridge bridge)
    {
        public ControlBridge Bridge { get; } = bridge;
        public string? ConfiguredSignature { get; set; }
        public DateTime LastHealthCheck { get; set; } = DateTime.UtcNow;
        public string? LastDebugSnapshot { get; set; }
        public DateTime LastSnapshotLog { get; set; } = DateTime.MinValue;
    }
}
sealed record ForegroundTarget(string ProcessName, int ProcessId, bool Is64Bit)
{
    public static ForegroundTarget? TryGet()
    {
        var hwnd = NativeWindow.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        NativeWindow.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0) return null;

        try
        {
            using var process = Process.GetProcessById((int)processId);
            if (!process.ProcessName.Equals("sai", StringComparison.OrdinalIgnoreCase) &&
                !process.ProcessName.Equals("sai2", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return new ForegroundTarget(process.ProcessName, process.Id, NativeWindow.IsProcess64Bit(process));
        }
        catch
        {
            return null;
        }
    }
}

sealed record BridgeLaunchSpec(string TargetName, int ProcessId, string InjectorPath, string PayloadPath)
{
    public static BridgeLaunchSpec? ForProcess(string bridgeRoot, ForegroundTarget target)
    {
        var platform = target.Is64Bit ? "win-x64" : "win-x86";
        var packagedDirectory = Path.Combine(bridgeRoot, platform);
        var injector = Path.Combine(packagedDirectory, "GestureCompanionBridge.Injector.exe");
        var payload = Path.Combine(packagedDirectory, "GestureCompanionBridge.NativePayload.dll");
        if (File.Exists(injector) && File.Exists(payload))
        {
            return new BridgeLaunchSpec(target.ProcessName, target.ProcessId, injector, payload);
        }

        // Development-tree fallback keeps local Debug runs convenient.
        var payloadDir = target.Is64Bit ? Path.Combine("x64", "Debug") : "Debug";
        injector = Path.Combine(bridgeRoot, "src", "Injector", "bin", "Debug", "net8.0", platform, "GestureCompanionBridge.Injector.exe");
        payload = Path.Combine(bridgeRoot, "src", "NativePayload", payloadDir, "GestureCompanionBridge.NativePayload.dll");
        return File.Exists(injector) && File.Exists(payload)
            ? new BridgeLaunchSpec(target.ProcessName, target.ProcessId, injector, payload)
            : null;
    }
}

sealed class ControlBridge : IDisposable
{
    private readonly Process _process;
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly object _gate = new();
    private bool _disposed;
    private bool _resourcesDisposed;

    public bool IsHealthy => !_disposed && !_process.HasExited;

    private ControlBridge(Process process, NamedPipeClientStream pipe, StreamReader reader, StreamWriter writer)
    {
        _process = process;
        _pipe = pipe;
        _reader = reader;
        _writer = writer;
    }

    public static async Task<ControlBridge> StartAsync(BridgeLaunchSpec spec)
    {
        var controlPipeName = "GestureCompanionBridgeControl-" + spec.TargetName + "-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = spec.InjectorPath,
                Arguments = $"--target {QuoteArg(spec.TargetName)} --pid {spec.ProcessId} --payload {QuoteArg(spec.PayloadPath)} --handshake-seconds 20 --control-pipe {QuoteArg(controlPipeName)}",
                UseShellExecute = false,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(spec.InjectorPath) ?? Environment.CurrentDirectory
            },
            EnableRaisingEvents = true
        };

        BridgeLog.Write($"Starting injector for {spec.TargetName}.exe pid={spec.ProcessId}: {spec.InjectorPath}");
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) BridgeLog.Write($"[{spec.TargetName} stdout] {e.Data}"); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) BridgeLog.Write($"[{spec.TargetName} stderr] {e.Data}"); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var pipe = new NamedPipeClientStream(".", controlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await pipe.ConnectAsync(cts.Token);

        var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var handshake = await reader.ReadLineAsync(cts.Token);
        if (!IsSuccessfulPayloadHandshake(handshake))
        {
            writer.Dispose();
            reader.Dispose();
            pipe.Dispose();
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(1000);
                }
            }
            catch
            {
                // The monitor will retry even if the failed injector has already exited.
            }
            finally
            {
                process.Dispose();
            }

            throw new InvalidDataException($"Bridge rejected payload handshake: {handshake ?? "<missing>"}");
        }

        return new ControlBridge(process, pipe, reader, writer);
    }

    private static bool IsSuccessfulPayloadHandshake(string? handshake)
    {
        if (string.IsNullOrWhiteSpace(handshake))
        {
            return false;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(handshake);
            var root = document.RootElement;
            return root.TryGetProperty("state", out var state) &&
                   state.GetString()?.Equals("ok", StringComparison.OrdinalIgnoreCase) == true &&
                   root.TryGetProperty("detail", out var detail) &&
                   detail.GetString()?.Equals("native-pointer-hook-installed", StringComparison.Ordinal) == true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
    public string? Send(string command)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            try
            {
                _writer.WriteLine(command);
                _writer.Flush();
                return _reader.ReadLine();
            }
            catch
            {
                _disposed = true;
                return null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_resourcesDisposed) return;
            if (!_disposed)
            {
                try { _writer.WriteLine("quit"); } catch { }
            }
            _disposed = true;
            _resourcesDisposed = true;
        }

        try { _process.WaitForExit(1000); } catch { }
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill();
                _process.WaitForExit(1000);
            }
        }
        catch { }
        try { _writer.Dispose(); } catch { }
        try { _reader.Dispose(); } catch { }
        try { _pipe.Dispose(); } catch { }
        try { _process.Dispose(); } catch { }
    }

    private static string QuoteArg(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}

static class NativeWindow
{
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public static bool IsProcess64Bit(Process process)
    {
        if (!Environment.Is64BitOperatingSystem) return false;
        if (!IsWow64Process(process.Handle, out var isWow64)) throw new Win32Exception(Marshal.GetLastWin32Error(), "IsWow64Process failed.");
        return !isWow64;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool wow64Process);
}










