using System;
using System.Reflection;
using System.Windows.Controls;
using Dynamo.Wpf.Extensions;
using DynamoCopilot.Core.Services;
using DynamoCopilot.Core.Settings;
using DynamoCopilot.Extension.Services;
using DynamoCopilot.Extension.ViewModels;
using DynamoCopilot.Extension.Views;

namespace DynamoCopilot.Extension
{
    public sealed class DynamoCopilotViewExtension : IViewExtension
    {
        public string UniqueId => "7A3E2F14-C591-4D8B-A7F2-90B3E1D54C6A";
        public string Name     => "Dynamo Co-pilot";
        private string TabName => "BimEra";

        private CopilotPanelViewModel? _viewModel;
        private CopilotPanelView?      _view;
        private ViewLoadedParams?      _loadedParams;
        private MenuItem?              _toggleMenuItem;
        private AuthService?           _authService;
        private bool                   _panelOpen = false;

        public void Startup(ViewStartupParams startupParams) { }

        public void Loaded(ViewLoadedParams loadedParams)
        {
            _loadedParams = loadedParams ?? throw new ArgumentNullException(nameof(loadedParams));

            // Catch any unhandled exception on the WPF dispatcher so we can log it
            // and keep Dynamo alive. Only suppress exceptions that originate from our
            // own code — anything else is re-raised.
            System.Windows.Application.Current.DispatcherUnhandledException += OnDispatcherUnhandledException;

            CreateView(loadedParams);

            CopilotLogger.Log("Extension loaded",
                $"server={(_viewModel != null ? "ViewModel OK" : "ViewModel NULL")}");

            // Run startup auth check without blocking the Loaded() call.
            // InitializeAsync updates ViewModel properties via OnPropertyChanged,
            // which triggers WPF bindings on the UI thread automatically.
            if (_viewModel != null)
                _ = _viewModel.InitializeAsync();

            // Add "Copilot" menu entry to Dynamo's menu bar
            try
            {
                var topMenu = new MenuItem { Header = TabName };
                _toggleMenuItem = new MenuItem { Header = Name };
                _toggleMenuItem.Click += OnTogglePanel;
                topMenu.Items.Add(_toggleMenuItem);
                loadedParams.dynamoMenu.Items.Add(topMenu);
            }
            catch { }
        }

        public void Shutdown()
        {
            try
            {
                if (System.Windows.Application.Current != null)
                    System.Windows.Application.Current.DispatcherUnhandledException -= OnDispatcherUnhandledException;
            }
            catch { }

            _viewModel?.Shutdown();
            _viewModel = null;
            _view      = null;
            _authService?.Dispose();
            _authService = null;
        }

        public void Dispose() => Shutdown();

        private void OnTogglePanel(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_loadedParams == null || _view == null) return;

            if (_panelOpen)
            {
                _loadedParams.CloseExtensioninInSideBar(this);
                _panelOpen = false;
                if (_toggleMenuItem != null) _toggleMenuItem.Header = "Show Panel";
            }
            else
            {
                _loadedParams.AddToExtensionsSideBar(this, _view);
                _panelOpen = true;
                if (_toggleMenuItem != null) _toggleMenuItem.Header = "Hide Panel";
            }
        }

        private void OnDispatcherUnhandledException(
            object sender,
            System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            var ex = e.Exception;
            // Only intercept exceptions that originate from our extension code.
            // Anything else is left for Dynamo's own handler.
            var trace = ex.StackTrace ?? string.Empty;
            bool isOurs = trace.IndexOf("DynamoCopilot", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isOurs && ex.InnerException != null)
                isOurs = (ex.InnerException.StackTrace ?? string.Empty)
                    .IndexOf("DynamoCopilot", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isOurs)
            {
                CopilotLogger.Log("DispatcherUnhandledException (handled)", ex);
                e.Handled = true; // Prevent Dynamo from crashing
                // Let the ViewModel surface the error gracefully
                try { _viewModel?.HandleRenderException(ex); } catch { }
            }
            else
            {
                // Not ours — log it but do not suppress
                CopilotLogger.Log("DispatcherUnhandledException (not ours — not handled)", ex);
            }
        }

        private void CreateView(ViewLoadedParams loadedParams)
        {
            var settings = DynamoCopilotSettings.Load();

            _authService = new AuthService(settings.EffectiveServerUrl);

            // ONNX embedding service for local node vector search (optional — degrades to BM25 if model not present)
            var onnxEmbedder = new OnnxEmbeddingService();
            var localSearch  = new LocalNodeSearchService(onnxEmbedder.IsReady ? onnxEmbedder : null);

            var historyService  = new ChatHistoryService();
            var currentPkgDir   = ResolveCurrentPackagesDir(loadedParams);
            var packageState    = new PackageStateService(currentPkgDir);
            var dynamoViewModel = loadedParams.DynamoWindow?.DataContext;
            var downloader      = new DynamoPackageDownloader(dynamoViewModel);

            _viewModel = new CopilotPanelViewModel(
                settings, _authService, localSearch,
                historyService, loadedParams, packageState, downloader);

            _view = new CopilotPanelView(_viewModel);
        }

        /// <summary>
        /// Tries to get the packages directory for the currently running Dynamo version
        /// from DynamoModel.PathManager via reflection.
        /// Falls back to null (PackageStateService will still scan all versions).
        /// </summary>
        private static string? ResolveCurrentPackagesDir(ViewLoadedParams loadedParams)
        {
            try
            {
                var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

                var dvmField = loadedParams.GetType().GetField("dynamoViewModel", flags);
                var dvm      = dvmField?.GetValue(loadedParams);
                if (dvm == null) return null;

                var model = dvm.GetType()
                    .GetProperty("Model", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(dvm);
                if (model == null) return null;

                var pathManager = model.GetType()
                    .GetProperty("PathManager", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(model);
                if (pathManager == null) return null;

                // IPathManager has DefaultPackagesDirectory (single) or PackagesDirectories (list)
                var defaultPkg = pathManager.GetType()
                    .GetProperty("DefaultPackagesDirectory",
                        BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(pathManager) as string;

                if (!string.IsNullOrEmpty(defaultPkg)) return defaultPkg;

                // Fallback: first entry of PackagesDirectories
                var dirs = pathManager.GetType()
                    .GetProperty("PackagesDirectories",
                        BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(pathManager) as System.Collections.IEnumerable;

                if (dirs != null)
                {
                    foreach (var d in dirs)
                    {
                        var s = d?.ToString();
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                }

                return null;
            }
            catch { return null; }
        }
    }
}
