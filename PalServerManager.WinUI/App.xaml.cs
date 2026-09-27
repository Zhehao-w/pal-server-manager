using HaoHaoTianTian.PalHR.Services;
using HaoHaoTianTian.PalHR.Models;
using HaoHaoTianTian.PalHR.ViewModels;
using HaoHaoTianTian.PalHR.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HaoHaoTianTian.PalHR;

public partial class App : Application
{
    public const string ProductName = "浩浩添添 Pal 人力资源部";
    public const string Version = ManagerProduct.Version;
    private static readonly HttpClient HttpClient = new();
    private readonly Mutex _singleInstance;
    private readonly bool _isPrimary;

    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        _singleInstance = new Mutex(true, @"Local\PalServerManager-Standalone-v2", out _isPrimary);
        InitializeComponent();
        UnhandledException += (_, args) => System.Diagnostics.Debug.WriteLine(args.Exception);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!_isPrimary)
        {
            BringExistingManagerForward();
            _singleInstance.Dispose();
            Environment.Exit(0);
            return;
        }
        ServerSetupWindow? setup = null;
        try
        {
            var safeFiles = new SafeFileService();
            var appPaths = AppPaths.ForCurrentInstall();
            StartupDiagnosticsService.Write(appPaths);
            var serverRegistry = new ServerRegistryService(appPaths, safeFiles);
            var serverState = new ServerStateService(appPaths, safeFiles);
            var firstRunSetup = new FirstRunServerSetupService(serverRegistry, serverState);
            var document = await serverRegistry.LoadAsync();
            var registered = document.Servers.FirstOrDefault(server => server.Id == document.SelectedServerId);
            var assessment = registered is null ? null : await serverState.AssessAsync(registered);
            if (assessment?.Kind != ServerStateKind.Ready || document.Servers.Count != 1)
            {
                setup = new ServerSetupWindow(serverRegistry, serverState, firstRunSetup);
                registered = await setup.ShowAsync();
                if (registered is null) return;
                var finalAssessment = await serverState.AssessAsync(registered);
                if (finalAssessment.Kind != ServerStateKind.Ready)
                    throw new InvalidOperationException(finalAssessment.Message);
            }
            var context = new ServerRootService().CreateRegisteredContext(appPaths, registered!);
            StartupDiagnosticsService.Write(appPaths, registered);
            var logging = new LoggingService(context);
            var processes = new ServerProcessService(context, logging);
            var rest = new PalRestApiService(context, HttpClient);
            var slots = new SaveSlotService(context, processes, logging, safeFiles);
            var worldSettings = new WorldSettingsService(context, logging, safeFiles);
            var initialRegistry = await slots.LoadAsync();
            await worldSettings.InitializeAsync(initialRegistry);
            var identity = new WorldIdentityAuditService(context, slots, worldSettings);
            await identity.AuditOrThrowAsync(initialRegistry);
            var backups = new BackupService(context, processes, slots, worldSettings, safeFiles, logging);
            var roster = new PlayerRosterService(context, safeFiles);
            var keepAwake = new KeepAwakeService(context, processes, logging);
            var worldOptions = new WorldOptionService(slots, logging);
            var builds = new PalServerBuildService(context, safeFiles);
            var journal = new OperationJournalService(context, safeFiles);
            var deletion = new DeleteWorldService(context, processes, slots, identity, backups, safeFiles, journal, logging);
            var discovery = new WorldDiscoveryService(context.ServerPaths, context.StatePaths);
            var importer = new WorldImportService(context, processes, slots, worldSettings, identity, discovery, worldOptions, journal, logging);
            var manager = new ServerManagerService(context, processes, rest, slots, worldSettings, keepAwake, backups, identity, worldOptions, builds, journal, safeFiles, new PowerService(), logging);
            StartupChoice? startupChoice = null;
            if (!processes.GetSnapshot().IsRunning)
            {
                var selectorViewModel = new SaveSelectorViewModel(slots, backups, worldSettings, deletion);
                var selector = new SaveSelectorWindow(selectorViewModel, worldSettings, slots, discovery, importer);
                var selectorChoice = selector.ShowAsync();
                setup?.Dismiss(); // successor window is active before setup closes
                setup = null;
                startupChoice = await selectorChoice;
                if (startupChoice is null) return;

                var selectedSaveViewModel = new MainViewModel(context, processes, rest, slots, backups, roster, manager, new SettingsService(context, safeFiles), worldSettings, keepAwake);
                MainWindow = new MainWindow(selectedSaveViewModel, worldSettings, manager, startupChoice);
                MainWindow.Activate();
                selector.Dismiss();
                return;
            }
            var viewModel = new MainViewModel(context, processes, rest, slots, backups, roster, manager, new SettingsService(context, safeFiles), worldSettings, keepAwake);
            MainWindow = new MainWindow(viewModel, worldSettings, manager, startupChoice);
            MainWindow.Activate();
            setup?.Dismiss();
        }
        catch (Exception exception)
        {
            setup?.Dismiss();
            var window = new Window { Title = ProductName };
            var panel = new StackPanel { Padding = new Thickness(28), Spacing = 12 };
            panel.Children.Add(new TextBlock { Text = "管理器无法启动", FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = exception.Message, TextWrapping = TextWrapping.Wrap, MaxWidth = 640 });
            window.Content = panel;
            window.Activate();
        }
    }

    private static void BringExistingManagerForward()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.MainWindowHandle == IntPtr.Zero) continue;
                    if (!string.Equals(process.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) continue;
                    var title = process.MainWindowTitle;
                    if (!title.StartsWith(ProductName, StringComparison.Ordinal) && !title.StartsWith("PalServer 管理器", StringComparison.Ordinal)) continue;
                    ShowWindow(process.MainWindowHandle, 9);
                    SetForegroundWindow(process.MainWindowHandle);
                    return;
                }
                catch { }
            }
        }
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
}
