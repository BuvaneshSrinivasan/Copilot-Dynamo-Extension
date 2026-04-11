using System;
using System.Windows.Controls;
using Dynamo.Wpf.Extensions;
using DynamoCopilot.Core.Services;
using DynamoCopilot.Core.Settings;
using DynamoCopilot.Extension.ViewModels;
using DynamoCopilot.Extension.Views;

namespace DynamoCopilot.Extension
{
    public sealed class DynamoCopilotViewExtension : IViewExtension
    {
        public string UniqueId => "7A3E2F14-C591-4D8B-A7F2-90B3E1D54C6A";
        public string Name     => "DynamoCopilot";

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

            CreateView(loadedParams);

            // Run startup auth check without blocking the Loaded() call.
            // InitializeAsync updates ViewModel properties via OnPropertyChanged,
            // which triggers WPF bindings on the UI thread automatically.
            if (_viewModel != null)
                _ = _viewModel.InitializeAsync();

            // Add "Copilot" menu entry to Dynamo's menu bar
            try
            {
                var topMenu = new MenuItem { Header = "Copilot" };
                _toggleMenuItem = new MenuItem { Header = "Dynamo Co-pilot" };
                _toggleMenuItem.Click += OnTogglePanel;
                topMenu.Items.Add(_toggleMenuItem);
                loadedParams.dynamoMenu.Items.Add(topMenu);
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

        private void CreateView(ViewLoadedParams loadedParams)
        {
            var settings = DynamoCopilotSettings.Load();

            // AuthService owns the HTTP client and token file.
            // ServerLlmService uses AuthService for every request.
            _authService = new AuthService(settings.ServerUrl);
            var llmService     = new ServerLlmService(settings.ServerUrl, _authService);
            var historyService = new ChatHistoryService();

            _viewModel = new CopilotPanelViewModel(
                settings, _authService, llmService, historyService, loadedParams);

            _view = new CopilotPanelView(_viewModel);
        }
    }
}
