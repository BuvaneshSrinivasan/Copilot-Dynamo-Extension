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
    public sealed class SuggestNodesViewExtension : IViewExtension
    {
        public string UniqueId => "A1F3D5B7-E924-4C8A-9D2F-6B0E8A4C2D1F";
        public string Name     => "Suggest Nodes";
        private string TabName => "BimEra";

        private SuggestNodesPanelViewModel? _viewModel;
        private SuggestNodesPanelView?      _view;
        private ViewLoadedParams?           _loadedParams;
        private MenuItem?                   _toggleMenuItem;
        private AuthService?                _authService;
        private bool                        _panelOpen = false;

        public void Startup(ViewStartupParams startupParams) { }

        public void Loaded(ViewLoadedParams loadedParams)
        {
            _loadedParams = loadedParams ?? throw new ArgumentNullException(nameof(loadedParams));

            CreateView(loadedParams);

            CopilotLogger.Log("SuggestNodesViewExtension loaded",
                $"viewModel={(_viewModel != null ? "OK" : "NULL")}");

            if (_viewModel != null)
                _ = _viewModel.InitializeAsync();

            if (_view != null)
            {
                _view.Loaded   += OnViewLoaded;
                _view.Unloaded += OnViewUnloaded;
            }

            try
            {
                var bimEraMenu  = FindOrCreateBimEraMenu(loadedParams.dynamoMenu.Items, TabName);
                _toggleMenuItem = new MenuItem { Header = Name };
                _toggleMenuItem.Click += OnTogglePanel;
                bimEraMenu.Items.Add(_toggleMenuItem);
            }
            catch { }
        }

        public void Shutdown()
        {
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
                _loadedParams.CloseExtensioninInSideBar(this);
            else
                _loadedParams.AddToExtensionsSideBar(this, _view);
        }

        private void OnViewLoaded(object _, System.Windows.RoutedEventArgs __)   => _panelOpen = true;
        private void OnViewUnloaded(object _, System.Windows.RoutedEventArgs __) => _panelOpen = false;

        private void CreateView(ViewLoadedParams loadedParams)
        {
            var settings = DynamoCopilotSettings.Load();

            _authService = new AuthService(settings.EffectiveServerUrl);

            var onnxEmbedder    = new OnnxEmbeddingService();
            var localSearch     = new LocalNodeSearchService(onnxEmbedder.IsReady ? onnxEmbedder : null);
            var currentPkgDir   = ResolveCurrentPackagesDir(loadedParams);
            var packageState    = new PackageStateService(currentPkgDir);
            var dynamoViewModel = loadedParams.DynamoWindow?.DataContext;
            var downloader      = new DynamoPackageDownloader(dynamoViewModel);

            _viewModel = new SuggestNodesPanelViewModel(
                _authService, localSearch, loadedParams, packageState, downloader);

            _view = new SuggestNodesPanelView(_viewModel);
        }

        /// <summary>
        /// Finds the "BimEra" top-level menu item if one already exists (added by the
        /// Copilot extension), or creates and registers a new one. This ensures both
        /// extensions share a single "BimEra" menu entry.
        /// </summary>
        private static MenuItem FindOrCreateBimEraMenu(
            System.Collections.IList menuItems, string tabName)
        {
            foreach (var item in menuItems)
            {
                if (item is MenuItem existing &&
                    existing.Header?.ToString() == tabName)
                    return existing;
            }

            var newMenu = new MenuItem { Header = tabName };
            menuItems.Add(newMenu);
            return newMenu;
        }

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

                var defaultPkg = pathManager.GetType()
                    .GetProperty("DefaultPackagesDirectory", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(pathManager) as string;

                if (!string.IsNullOrEmpty(defaultPkg)) return defaultPkg;

                var dirs = pathManager.GetType()
                    .GetProperty("PackagesDirectories", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(pathManager) as System.Collections.IEnumerable;

                if (dirs != null)
                    foreach (var d in dirs)
                    {
                        var s = d?.ToString();
                        if (!string.IsNullOrEmpty(s)) return s;
                    }

                return null;
            }
            catch { return null; }
        }
    }
}
