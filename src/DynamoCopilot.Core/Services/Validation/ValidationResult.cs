using System.Collections.Generic;

namespace DynamoCopilot.Core.Services.Validation
{
    public sealed class ValidationResult
    {
        public bool IsValid { get; set; }
        public List<ValidationIssue> Issues { get; set; } = new List<ValidationIssue>();

        public static ValidationResult Ok() =>
            new ValidationResult { IsValid = true };
    }
}
