using System.Text;

namespace DynamoCopilot.Core.Services.Validation
{
    /// <summary>
    /// Builds escalating LLM prompts to auto-fix invalid Revit enum values in generated code.
    /// Attempt 0 = conservative (list specific bad values).
    /// Attempt 1 = aggressive (rewrite enums or use integer literals).
    /// </summary>
    public static class AutoFixRequestBuilder
    {
        public static string Build(string originalCode, ValidationResult result, int attempt)
        {
            var sb = new StringBuilder();

            if (attempt == 0)
            {
                sb.AppendLine(
                    "The following Revit API enum values in the code below do not exist " +
                    "in this Revit installation and will cause a NameError at runtime:");
                sb.AppendLine();
                foreach (var issue in result.Issues)
                    sb.AppendLine($"  - {issue.Category}.{issue.InvalidValue}");
                sb.AppendLine();
                sb.AppendLine(
                    "Please correct ONLY those invalid enum values to valid ones that exist in " +
                    "Revit's API, keeping all other code exactly the same. " +
                    "Return the COMPLETE corrected script in a single ```python ... ``` block.");
            }
            else
            {
                sb.AppendLine(
                    "The previous fix attempt still contains invalid Revit API enum values. " +
                    "The following values are still invalid:");
                sb.AppendLine();
                foreach (var issue in result.Issues)
                    sb.AppendLine($"  - {issue.Category}.{issue.InvalidValue}");
                sb.AppendLine();
                sb.AppendLine(
                    "Please rewrite the code to avoid these invalid values entirely. " +
                    "Use valid BuiltInParameter / BuiltInCategory / UnitTypeId values, " +
                    "or use their integer literals as a fallback (e.g. BuiltInParameter.INVALID_VALUE " +
                    "→ the integer value). Return the COMPLETE fixed script in a single ```python ... ``` block.");
            }

            sb.AppendLine();
            sb.AppendLine("Current code:");
            sb.AppendLine("```python");
            sb.AppendLine(originalCode);
            sb.AppendLine("```");

            return sb.ToString();
        }
    }
}
