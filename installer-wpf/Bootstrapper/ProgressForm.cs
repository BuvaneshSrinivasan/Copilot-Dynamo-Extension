using System;
using System.Drawing;
using System.Windows.Forms;

namespace DynamoCopilot.Bootstrapper
{
    internal class ProgressForm : Form
    {
        private readonly Label _label;
        private readonly ProgressBar _bar;

        internal ProgressForm()
        {
            Text = "DynamoCopilot Setup";
            Width = 440;
            Height = 140;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(30, 30, 30);

            _label = new Label
            {
                Left = 16, Top = 16, Width = 396, Height = 22,
                Text = "Please wait...",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f)
            };

            _bar = new ProgressBar
            {
                Left = 16, Top = 48, Width = 396, Height = 26,
                Style = ProgressBarStyle.Marquee
            };

            Controls.Add(_label);
            Controls.Add(_bar);
        }

        internal void SetStatus(string text)
        {
            if (InvokeRequired) { Invoke(new Action(() => SetStatus(text))); return; }
            _label.Text = text;
        }

        internal void SetProgress(int percent)
        {
            if (InvokeRequired) { Invoke(new Action(() => SetProgress(percent))); return; }
            if (percent < 0)
            {
                _bar.Style = ProgressBarStyle.Marquee;
            }
            else
            {
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Value = Math.Min(percent, 100);
            }
        }
    }
}
