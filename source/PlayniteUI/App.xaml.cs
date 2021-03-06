﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using CefSharp;
using Playnite;
using Playnite.Database;
using PlayniteUI.Windows;
using PlayniteUI.ViewModels;
using System.Threading.Tasks;
using Playnite.Services;
using System.Windows.Markup;
using System.IO;
using System.Windows.Input;
using System.ComponentModel;
using Playnite.Metadata;
using Playnite.API;
using PlayniteUI.API;
using Playnite.Plugins;
using Playnite.Scripting;
using Playnite.App;
using Playnite.Controllers;
using Playnite.Settings;
using Playnite.SDK;
using PlayniteUI.WebView;
using Newtonsoft.Json;

namespace PlayniteUI
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application, INotifyPropertyChanged, IPlayniteApplication
    {
        private static ILogger logger = LogManager.GetLogger();
        private const string instanceMuxet = "PlayniteInstaceMutex";
        private Mutex appMutex;
        private bool resourcesReleased = false;
        private PipeService pipeService;
        private PipeServer pipeServer;
        private XInputDevice xdevice;
        private DialogsFactory dialogs;
        private GameControllerFactory controllers;

        public IWindowFactory MainViewWindow
        {
            get; private set;
        }

        public System.Version CurrentVersion
        {
            get => Updater.GetCurrentVersion();
        }

        public PlayniteAPI Api
        {
            get; set;
        }

        public ExtensionFactory Extensions
        {
            get; set;
        }
        
        public event PropertyChangedEventHandler PropertyChanged;

        public static MainViewModel MainModel
        {
            get;
            private set;
        }

        public static FullscreenViewModel FullscreenModel
        {
            get;
            private set;
        }

        public static GameDatabase Database
        {
            get;
            private set;
        }

        public static GamesEditor GamesEditor
        {
            get;
            private set;
        }

        public static PlayniteSettings AppSettings
        {
            get;
            private set;
        }

        public static App CurrentApp
        {
            get => Current as App;
        }

        private bool isActive;
        public bool IsActive
        {
            get => isActive;
            set
            {
                isActive = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IsActive"));
            }
        }

        public App()
        {
            InitializeComponent();
        }

        private void Application_SessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            logger.Info("Shutting down application because of session ending.");
            Quit();
        }

        private void Application_Exit(object sender, ExitEventArgs e)
        {
            ReleaseResources();
            appMutex?.ReleaseMutex();
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = (Exception)e.ExceptionObject;
            logger.Error(exception, "Unhandled exception occured.");

            var model = new CrashHandlerViewModel(
                CrashHandlerWindowFactory.Instance, dialogs, new ResourceProvider());
            model.Exception = exception.ToString();
            model.OpenView();
            Process.GetCurrentProcess().Kill();
        }

        private void Application_Startup(object sender, StartupEventArgs e)
        {
#if !DEBUG
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
#endif    

            // Multi-instance checking
            if (Mutex.TryOpenExisting(instanceMuxet, out var mutex))
            {
                try
                {
                    var client = new PipeClient(PlayniteSettings.GetAppConfigValue("PipeEndpoint"));
                    if (e.Args.Count() > 0 && e.Args.Contains("-command"))
                    {
                        var commandArgs = e.Args[1].Split(new char[] { ':' });
                        var command = commandArgs[0];
                        client.InvokeCommand(command, commandArgs.Count() > 1 ? commandArgs[1] : string.Empty);
                    }
                    else
                    {
                        client.InvokeCommand(CmdlineCommands.Focus, string.Empty);
                    }
                }
                catch (Exception exc) when (!PlayniteEnvironment.ThrowAllErrors)
                {
                    PlayniteMessageBox.Show(
                        ResourceProvider.Instance.FindString("LOCStartGenericError"),
                        ResourceProvider.Instance.FindString("LOCStartupError"), MessageBoxButton.OK, MessageBoxImage.Error);
                    logger.Error(exc, "Can't process communication with other instances.");
                }

                logger.Info("Application already running, shutting down.");
                Quit();
                return;
            }
            else
            {
                appMutex = new Mutex(true, instanceMuxet);
            }

            // Migrate library configuration
            PlayniteSettings.MigrateSettingsConfig();

            Time.Instance = new Time();
            AppSettings = PlayniteSettings.LoadSettings();
            Localization.SetLanguage(AppSettings.Language);
            Resources.Remove("AsyncImagesEnabled");
            Resources.Add("AsyncImagesEnabled", AppSettings.AsyncImageLoading);
            if (AppSettings.DisableHwAcceleration)
            {
                System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            }

            CefTools.ConfigureCef();
            dialogs = new DialogsFactory(AppSettings.StartInFullscreen);

            // Create directories
            try
            {
                ExtensionFactory.CreatePluginFolders();
            }
            catch (Exception exc) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(exc, "Failed to script and plugin directories.");
            }

            // Initialize API
            Database = new GameDatabase();
            controllers = new GameControllerFactory(Database);
            Api = new PlayniteAPI(
                new DatabaseAPI(Database),
                dialogs,
                null,
                new PlayniteInfoAPI(),
                new PlaynitePathsAPI(),
                new WebViewFactory(),
                new ResourceProvider());
            Extensions = new ExtensionFactory(Database, controllers);

            // Load theme
            ApplyTheme(AppSettings.Skin, AppSettings.SkinColor, false);            

            // First run wizard
            bool isFirstStart = !AppSettings.FirstTimeWizardComplete;
            bool existingDb = false;
            if (!AppSettings.FirstTimeWizardComplete)
            {
                var wizardWindow = FirstTimeStartupWindowFactory.Instance;
                var wizardModel = new FirstTimeStartupViewModel(wizardWindow, dialogs, new ResourceProvider(), Extensions, Api);
                if (wizardModel.OpenView() == true)
                {
                    var settings = wizardModel.Settings;
                    AppSettings.FirstTimeWizardComplete = true;
                    if (wizardModel.DatabaseLocation == FirstTimeStartupViewModel.DbLocation.Custom)
                    {
                        AppSettings.DatabasePath = settings.DatabasePath;
                    }
                    else
                    {
                        if (PlayniteSettings.IsPortable)
                        {
                            AppSettings.DatabasePath = "games.db";
                        }
                        else
                        {
                            AppSettings.DatabasePath = Path.Combine(PlaynitePaths.ConfigRootPath, "games.db");
                        }
                    }

                    AppSettings.DisabledPlugins = settings.DisabledPlugins;
                    AppSettings.SaveSettings();
                    existingDb = File.Exists(AppSettings.DatabasePath);
                    Database.SetDatabasePath(AppSettings.DatabasePath);
                    Database.OpenDatabase();

                    if (wizardModel.ImportedGames?.Any() == true)
                    {
                        InstalledGamesViewModel.AddImportableGamesToDb(wizardModel.ImportedGames, Database);
                    }
                }
                else
                {
                    AppSettings.DatabasePath = Path.Combine(PlaynitePaths.ConfigRootPath, "games.db");
                    AppSettings.SaveSettings();
                    existingDb = File.Exists(AppSettings.DatabasePath);
                    Database.SetDatabasePath(AppSettings.DatabasePath);
                }
            }
            else
            {
                Database.SetDatabasePath(AppSettings.DatabasePath);
            }

            // Emulator wizard
            if (!AppSettings.EmulatorWizardComplete)
            {
                var model = new EmulatorImportViewModel(Database,
                       EmulatorImportViewModel.DialogType.Wizard,
                       EmulatorImportWindowFactory.Instance,
                       dialogs,
                       new ResourceProvider());

                model.OpenView();                
                AppSettings.EmulatorWizardComplete = true;
                AppSettings.SaveSettings();
            }

            Extensions.LoadLibraryPlugins(Api, AppSettings.DisabledPlugins);
            Extensions.LoadGenericPlugins(Api, AppSettings.DisabledPlugins);
            Extensions.LoadScripts(Api, AppSettings.DisabledPlugins);
            GamesEditor = new GamesEditor(Database, controllers, AppSettings, dialogs, Extensions);
            CustomImageStringToImageConverter.Database = Database;

            // Main view startup
            if (AppSettings.StartInFullscreen)
            {
                OpenFullscreenView(true);
            }
            else
            {
                OpenNormalView(isFirstStart, existingDb);
            }

            // Update and stats
            CheckUpdate();
            SendUsageData();

            // Pipe server
            pipeService = new PipeService();
            pipeService.CommandExecuted += PipeService_CommandExecuted;
            pipeServer = new PipeServer(PlayniteSettings.GetAppConfigValue("PipeEndpoint"));
            pipeServer.StartServer(pipeService);

            var args = Environment.GetCommandLineArgs();
            if (args.Count() > 0 && args.Contains("-command"))
            {
                var commandArgs = args[2].Split(new char[] { ':' });
                var command = commandArgs[0];
                var cmdArgs = commandArgs.Count() > 1 ? commandArgs[1] : string.Empty;
                PipeService_CommandExecuted(this, new CommandExecutedEventArgs(command, cmdArgs));
            }

            xdevice = new XInputDevice(InputManager.Current)
            {
                SimulateAllKeys = true,
                SimulateNavigationKeys = true
            };

            logger.Info($"Application {CurrentVersion} started");
        }

        private void PipeService_CommandExecuted(object sender, CommandExecutedEventArgs args)
        {
            logger.Info($"Executing command \"{args.Command}\" from pipe with arguments \"{args.Args}\"");

            switch (args.Command)
            {
                case CmdlineCommands.Focus:                    
                    MainModel?.RestoreWindow();
                    FullscreenModel?.RestoreWindow();
                    break;

                case CmdlineCommands.Launch:
                    var game = Database.GamesCollection.FindById(int.Parse(args.Args));
                    if (game == null)
                    {
                        logger.Error($"Cannot start game, game {args.Args} not found.");
                    }
                    else
                    {
                        GamesEditor.PlayGame(game);
                    }

                    break;

                default:
                    logger.Warn("Unknown command received");
                    break;
            }
        }

        private async void CheckUpdate()
        {
            await Task.Run(async () =>
            {
                await Task.Delay(Playnite.Timer.SecondsToMilliseconds(10));
                if (GlobalTaskHandler.IsActive)
                {
                    GlobalTaskHandler.Wait();
                }

                var updater = new Updater(this);

                while (true)
                {
                    if (!UpdateViewModel.InstanceInUse)
                    {
                        try
                        {
                            if (updater.IsUpdateAvailable)
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    var model = new UpdateViewModel(updater, UpdateWindowFactory.Instance, new ResourceProvider(), dialogs);
                                    model.OpenView();
                                });
                            }
                        }
                        catch (Exception exc) when (!PlayniteEnvironment.ThrowAllErrors)
                        {
                            logger.Error(exc, "Failed to process update.");
                        }
                    }

                    await Task.Delay(Playnite.Timer.HoursToMilliseconds(4));
                }
            });
        }

        private async void SendUsageData()
        {
            await Task.Run(() =>
            {
                try
                {
                    var client = new ServicesClient();
                    client.PostUserUsage(AppSettings.InstallInstanceId);
                }
                catch (Exception exc) when (!PlayniteEnvironment.ThrowAllErrors)
                {
                    logger.Error(exc, "Failed to post user usage data.");
                }
            });
        }

        public void Restart()
        {
            ReleaseResources();
            Process.Start(PlaynitePaths.ExecutablePath);
            Shutdown(0);
        }

        public void Quit()
        {
            logger.Info("Shutting down Playnite");
            ReleaseResources();
            Shutdown(0);
        }

        private void ReleaseResources()
        {
            logger.Debug("Releasing Playnite resources...");
            if (resourcesReleased)
            {
                return;
            }

            var progressModel = new ProgressViewViewModel(new ProgressWindowFactory(), () =>
            {
                try
                {
                    GlobalTaskHandler.CancelAndWait();                    
                    GamesEditor?.Dispose();
                    AppSettings?.SaveSettings();
                    Extensions?.Dispose();
                    controllers?.Dispose();
                    Database?.CloseDatabase();
                }
                catch (Exception exc) when (!PlayniteEnvironment.ThrowAllErrors)
                {
                    logger.Error(exc, "Failed to dispose Playnite objects.");
                }
            }, ResourceProvider.Instance.FindString("LOCClosingPlaynite"));

            progressModel.ActivateProgress();

            // These must run on main thread
            if (Cef.IsInitialized)
            {
                Cef.Shutdown();
            }

            resourcesReleased = true;
        }

        public async void OpenNormalView(bool isFirstStart, bool existingDb)
        {
            logger.Debug("Opening Desktop view");
            if (Database.IsOpen)
            {
                FullscreenModel = null;
                Database.CloseDatabase();
            }

            GamesEditor.IsFullscreen = false;
            dialogs.IsFullscreen = false;
            ApplyTheme(AppSettings.Skin, AppSettings.SkinColor, false);
            MainViewWindow = new MainWindowFactory();
            MainModel = new MainViewModel(
                Database,
                MainViewWindow,
                dialogs,
                new ResourceProvider(),
                AppSettings,
                GamesEditor,
                Api,
                Extensions);
            Api.MainView = new MainViewAPI(MainModel);
            MainModel.OpenView();
            Current.MainWindow = MainViewWindow.Window;
            if (AppSettings.UpdateLibStartup)
            {
                await MainModel.UpdateDatabase(AppSettings.UpdateLibStartup, !isFirstStart);
            }

            if (isFirstStart && !existingDb)
            {
                var metaSettings = new MetadataDownloaderSettings();
                metaSettings.ConfigureFields(MetadataSource.StoreOverIGDB, true);
                metaSettings.CoverImage.Source = MetadataSource.IGDBOverStore;
                metaSettings.Name = new MetadataFieldSettings(true, MetadataSource.Store);
                await MainModel.DownloadMetadata(metaSettings);
            }
        }

        public async void OpenFullscreenView(bool updateDb)
        {
            logger.Debug("Opening Fullscreen view");
            if (Database.IsOpen)
            {
                MainModel = null;
                Database.CloseDatabase();
            }

            GamesEditor.IsFullscreen = true;
            dialogs.IsFullscreen = true;
            ApplyTheme(AppSettings.SkinFullscreen, AppSettings.SkinColorFullscreen, true);
            MainViewWindow = new FullscreenWindowFactory();
            FullscreenModel = new FullscreenViewModel(
                Database,
                MainViewWindow,
                dialogs,
                new ResourceProvider(),
                AppSettings,
                GamesEditor,
                Api,
                Extensions);
            Api.MainView = new MainViewAPI(MainModel);
            FullscreenModel.OpenView(!PlayniteEnvironment.IsDebugBuild);
            Current.MainWindow = MainViewWindow.Window;

            if (updateDb)
            {
                await FullscreenModel.UpdateDatabase(AppSettings.UpdateLibStartup, true);
            }            
        }

        private void ApplyTheme(string name, string profile, bool fullscreen)
        {
            bool isThemeValid = true;
            string themeName = "Classic";
            string themeProfile = "Default";

            if (fullscreen)
            {
                if (Themes.CurrentFullscreenTheme == name && Themes.CurrentFullscreenColor == profile)
                {
                    return;
                }
            }
            else
            {
                if (Themes.CurrentTheme == name && Themes.CurrentColor == profile)
                {
                    return;
                }
            }

            var themeValid = Themes.IsThemeValid(name, fullscreen);
            if (themeValid.Item1 == false)
            {
                PlayniteMessageBox.Show(
                    string.Format(ResourceProvider.Instance.FindString("LOCSkinApplyError"), AppSettings.Skin, AppSettings.SkinColor, themeValid.Item2),
                    ResourceProvider.Instance.FindString("LOCSkinError"), MessageBoxButton.OK, MessageBoxImage.Error);
                isThemeValid = false;
            }

            var profileValid = Themes.IsColorProfileValid(name, profile, fullscreen);
            if (profileValid.Item1 == false)
            {
                PlayniteMessageBox.Show(
                    string.Format(ResourceProvider.Instance.FindString("LOCSkinApplyError"), AppSettings.Skin, AppSettings.SkinColor, profileValid.Item2),
                    ResourceProvider.Instance.FindString("LOCSkinError"), MessageBoxButton.OK, MessageBoxImage.Error);
                isThemeValid = false;
            }

            if (isThemeValid)
            {
                themeName = name;
                themeProfile = profile;
            }

            logger.Debug($"Applying theme {themeName}, {themeProfile}, {fullscreen}");

            if (fullscreen)
            {
                Themes.ApplyFullscreenTheme(themeName, themeProfile, true);
            }
            else
            {
                Themes.ApplyTheme(themeName, themeProfile, true);
            }
        }

        private void Application_Activated(object sender, EventArgs e)
        {
            IsActive = true;
        }

        private void Application_Deactivated(object sender, EventArgs e)
        {
            IsActive = false;
        }
    }
}
