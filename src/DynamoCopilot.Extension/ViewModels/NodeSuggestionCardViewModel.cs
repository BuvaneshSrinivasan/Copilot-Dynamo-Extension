using System.ComponentModel;
using System.Runtime.CompilerServices;
using DynamoCopilot.Core.Models;

namespace DynamoCopilot.Extension.ViewModels
{
    /// <summary>
    /// ViewModel for a single node suggestion card in the "Suggest Nodes" panel.
    /// Exposes flat string properties ready for binding and supports an
    /// expand/collapse toggle to show port and description details.
    /// </summary>
    public sealed class NodeSuggestionCardViewModel : INotifyPropertyChanged
    {
        private bool _isExpanded;

        // ── Display properties ────────────────────────────────────────────────

        public string Name        { get; }
        public string Category    { get; }
        public string PackageName { get; }
        public string Description { get; }

        /// <summary>Comma-separated input port names, or empty string.</summary>
        public string InputPorts  { get; }

        /// <summary>Comma-separated output port names, or empty string.</summary>
        public string OutputPorts { get; }

        /// <summary>Similarity score formatted as a percentage, e.g. "87%".</summary>
        public string ScoreText   { get; }

        /// <summary>
        /// Gemini's one-sentence reason for recommending this node.
        /// Empty when the server fell back to pure vector ranking.
        /// </summary>
        public string Reason      { get; }

        // ── Visibility helpers ────────────────────────────────────────────────

        public bool HasReason      => !string.IsNullOrWhiteSpace(Reason);
        public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
        public bool HasPorts       => !string.IsNullOrWhiteSpace(InputPorts)
                                   || !string.IsNullOrWhiteSpace(OutputPorts);

        /// <summary>True when the card should show description and port details.</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(); }
        }

        // ── Construction ──────────────────────────────────────────────────────

        public NodeSuggestionCardViewModel(NodeSuggestion node)
        {
            Name        = node.Name;
            Category    = node.Category    ?? string.Empty;
            PackageName = node.PackageName ?? string.Empty;
            Description = node.Description ?? string.Empty;
            Reason      = node.Reason      ?? string.Empty;
            ScoreText   = node.Score > 0f ? $"{node.Score:P0}" : string.Empty;

            InputPorts  = node.InputPorts  != null && node.InputPorts.Length  > 0
                ? string.Join(", ", node.InputPorts)
                : string.Empty;

            OutputPorts = node.OutputPorts != null && node.OutputPorts.Length > 0
                ? string.Join(", ", node.OutputPorts)
                : string.Empty;
        }

        // ── INotifyPropertyChanged ────────────────────────────────────────────

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
