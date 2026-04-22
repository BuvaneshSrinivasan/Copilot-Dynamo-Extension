namespace DynamoCopilot.Core.Services.Validation
{
    public sealed class ValidationIssue
    {
        /// <summary>"Error" (blocks injection) or "Warning" (advisory only).</summary>
        public string Severity     { get; set; } = "Warning";
        /// <summary>Enum type: "BuiltInParameter", "BuiltInCategory", or "UnitTypeId".</summary>
        public string Category     { get; set; } = string.Empty;
        /// <summary>The exact invalid value used in the generated code.</summary>
        public string InvalidValue { get; set; } = string.Empty;
        public string Message      { get; set; } = string.Empty;
    }
}
