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
        public string Name => "DynamoCopilot";

        private CopilotPanelViewModel? _viewModel;
        private CopilotPanelView? _view;
        private ViewLoadedParams? _loadedParams;
        private MenuItem? _toggleMenuItem;
        private bool _panelOpen = false;

        public void Startup(ViewStartupParams startupParams) { }

        public void Loaded(ViewLoadedParams loadedParams)
        {
            _loadedParams = loadedParams ?? throw new ArgumentNullException(nameof(loadedParams));

            CreateView(loadedParams);

            // Add a new top-level "Dynamo Co-pilot" menu tab to the Dynamo menu bar
            try
            {
                var topMenu = new MenuItem { Header = "BimEra" };

                _toggleMenuItem = new MenuItem { Header = "Dynamo Co-pilot" };
                _toggleMenuItem.Click += OnTogglePanel;
                topMenu.Items.Add(_toggleMenuItem);

                loadedParams.dynamoMenu.Items.Add(topMenu);
            }
            catch { }

            // Also add to the Extensions (star) sidebar menu as a fallback entry
            // try
            // {
            //     loadedParams.AddExtensionMenuItem(new MenuItem
            //     {
            //         Header = "Show/Hide DynamoCopilot",
            //     });
            // }
            // catch { }
        }

        public void Shutdown()
        {
            _viewModel?.Shutdown();
            _viewModel = null;
            _view = null;
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
            var llmService = LlmServiceFactory.Create(settings);
            var historyService = new ChatHistoryService();

            _viewModel = new CopilotPanelViewModel(
                settings, llmService, historyService, loadedParams);

            _view = new CopilotPanelView(_viewModel);
        }
    }
}
