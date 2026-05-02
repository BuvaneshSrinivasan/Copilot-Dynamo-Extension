using System;
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

            if (System.Windows.Application.Current != null)
                System.Windows.Application.Current.DispatcherUnhandledException += OnDispatcherUnhandledException;

            CreateView(loadedParams);

            CopilotLogger.Log("Extension loaded",
                $"server={(_viewModel != null ? "ViewModel OK" : "ViewModel NULL")}");

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
                _loadedParams.CloseExtensioninInSideBar(this);
            else
                _loadedParams.AddToExtensionsSideBar(this, _view);
        }

        private void OnViewLoaded(object _, System.Windows.RoutedEventArgs __)   => _panelOpen = true;
        private void OnViewUnloaded(object _, System.Windows.RoutedEventArgs __) => _panelOpen = false;

        private void OnDispatcherUnhandledException(
            object sender,
            System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            var ex = e.Exception;
            var trace = ex.StackTrace ?? string.Empty;
            bool isOurs = trace.IndexOf("DynamoCopilot", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isOurs && ex.InnerException != null)
                isOurs = (ex.InnerException.StackTrace ?? string.Empty)
                    .IndexOf("DynamoCopilot", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isOurs)
            {
                CopilotLogger.Log("DispatcherUnhandledException (handled)", ex);
                e.Handled = true;
                try { _viewModel?.HandleRenderException(ex); } catch { }
            }
            else
            {
                CopilotLogger.Log("DispatcherUnhandledException (not ours — not handled)", ex);
            }
        }

        private void CreateView(ViewLoadedParams loadedParams)
        {
            var settings = DynamoCopilotSettings.Load();

            _authService = new AuthService(settings.EffectiveServerUrl);

            var historyService = new ChatHistoryService();

            _viewModel = new CopilotPanelViewModel(
                settings, _authService, historyService, loadedParams);

            _view = new CopilotPanelView(_viewModel);
        }

        /// <summary>
        /// Finds the "BimEra" top-level menu item if one already exists (added by the
        /// Suggest Nodes extension), or creates and registers a new one. Both extensions
        /// share a single "BimEra" menu entry regardless of load order.
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
    }
}
