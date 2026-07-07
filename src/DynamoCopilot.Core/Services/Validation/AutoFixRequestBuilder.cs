using System.Linq;
using System.Text;

namespace DynamoCopilot.Core.Services.Validation
{
    /// <summary>
    /// Builds escalating LLM prompts to auto-fix invalid Revit API references in generated code —
    /// either bad enum-style constants (BuiltInParameter/BuiltInCategory/UnitTypeId, from
    /// <see cref="RevitEnumValidator"/>) or invented members on Revit static helper classes
    /// (from <see cref="RevitApiSurfaceValidator"/>).
    /// Attempt 0 = conservative (list specific bad values).
    /// Attempt 1 = aggressive (rewrite the offending references entirely).
    /// </summary>
    public static class AutoFixRequestBuilder
    {
        public static string Build(string originalCode, ValidationResult result, int attempt)
        {
            var sb = new StringBuilder();
            bool hasEnumIssues   = result.Issues.Any(i => i.Category != "ApiMember");
            bool hasMemberIssues = result.Issues.Any(i => i.Category == "ApiMember");
            string kind = hasEnumIssues && hasMemberIssues ? "enum values and API members"
                        : hasMemberIssues ? "API members"
                        : "enum values";

            if (attempt == 0)
            {
                sb.AppendLine(
                    $"The following Revit API {kind} in the code below do not exist " +
                    "in this Revit installation and will cause a runtime error:");
                sb.AppendLine();
                foreach (var issue in result.Issues)
                    sb.AppendLine(issue.Category == "ApiMember" ? $"  - {issue.InvalidValue}" : $"  - {issue.Category}.{issue.InvalidValue}");
                sb.AppendLine();
                sb.AppendLine(
                    $"Please correct ONLY those invalid {kind} to ones that actually exist in " +
                    "Revit's API, keeping all other code exactly the same. " +
                    "Return the COMPLETE corrected script in a single ```python ... ``` block.");
            }
            else
            {
                sb.AppendLine(
                    $"The previous fix attempt still contains invalid Revit API {kind}. " +
                    "The following are still invalid:");
                sb.AppendLine();
                foreach (var issue in result.Issues)
                    sb.AppendLine(issue.Category == "ApiMember" ? $"  - {issue.InvalidValue}" : $"  - {issue.Category}.{issue.InvalidValue}");
                sb.AppendLine();
                sb.AppendLine(
                    "Please rewrite the code to avoid these invalid references entirely. " +
                    "For enum values, use valid BuiltInParameter / BuiltInCategory / UnitTypeId values, " +
                    "or their integer literals as a fallback. For invented API members, use a real " +
                    "documented method/property on that class, or an alternative approach entirely. " +
                    "Return the COMPLETE fixed script in a single ```python ... ``` block.");
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
