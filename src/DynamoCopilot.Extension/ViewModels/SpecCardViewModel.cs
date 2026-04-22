using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using DynamoCopilot.Core.Models;
using DynamoCopilot.Extension.Commands;

namespace DynamoCopilot.Extension.ViewModels
{
    // ── Per-question ViewModel (two-way bindable Answer) ─────────────────────────

    public sealed class ClarifyingQuestionViewModel : INotifyPropertyChanged
    {
        private string _answer = string.Empty;

        public string Question { get; }
        public ObservableCollection<string> Options { get; }

        public string Answer
        {
            get => _answer;
            set { _answer = value; OnPropertyChanged(); }
        }

        public ClarifyingQuestionViewModel(ClarifyingQuestion source)
        {
            Question = source.Question;
            Options  = new ObservableCollection<string>(source.Options);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ── Spec card ViewModel ───────────────────────────────────────────────────────

    public sealed class SpecCardViewModel : INotifyPropertyChanged
    {
        private readonly Func<CodeSpecification, Task> _onConfirm;
        private readonly Action _onCancel;

        public CodeSpecification Spec { get; }

        public ObservableCollection<ClarifyingQuestionViewModel> Questions { get; }
            = new ObservableCollection<ClarifyingQuestionViewModel>();

        public bool HasQuestions => Questions.Count > 0;

        // Summary text shown in the card header
        public string StepsSummary
        {
            get
            {
                if (Spec.Steps.Count == 0) return string.Empty;
                var sb = new StringBuilder();
                for (int i = 0; i < Spec.Steps.Count; i++)
                    sb.AppendLine($"{i + 1}. {Spec.Steps[i]}");
                return sb.ToString().TrimEnd();
            }
        }

        public string OutputSummary =>
            string.IsNullOrWhiteSpace(Spec.Output?.Description)
                ? Spec.Output?.Type ?? string.Empty
                : $"{Spec.Output.Type}: {Spec.Output.Description}";

        public bool HasInputs => Spec.Inputs.Count > 0;

        public ICommand ConfirmCommand { get; }
        public ICommand CancelCommand  { get; }

        public SpecCardViewModel(
            CodeSpecification spec,
            Func<CodeSpecification, Task> onConfirm,
            Action onCancel)
        {
            Spec       = spec ?? throw new ArgumentNullException(nameof(spec));
            _onConfirm = onConfirm;
            _onCancel  = onCancel;

            foreach (var q in spec.Questions)
                Questions.Add(new ClarifyingQuestionViewModel(q));

            ConfirmCommand = new RelayCommand(OnConfirm);
            CancelCommand  = new RelayCommand(_onCancel);
        }

        private void OnConfirm()
        {
            // Merge answers back into the spec questions before handing off
            for (int i = 0; i < Questions.Count && i < Spec.Questions.Count; i++)
                Spec.Questions[i].Answer = Questions[i].Answer;

            _ = _onConfirm(Spec);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

}
