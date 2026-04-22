using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DynamoCopilot.Core.Services.Validation
{
    /// <summary>
    /// Validates generated Python code against Revit's actual runtime enum values.
    /// Checks BuiltInParameter, BuiltInCategory, and UnitTypeId by reflecting on the
    /// RevitAPI assembly (always loaded in the Dynamo-for-Revit process).
    /// Silently skips validation if the assembly is not present (e.g. unit tests, standalone).
    /// </summary>
    public sealed class RevitEnumValidator
    {
        public static readonly RevitEnumValidator Instance = new RevitEnumValidator();

        private static readonly Regex BipRegex  =
            new Regex(@"\bBuiltInParameter\.([A-Za-z_][A-Za-z0-9_]*)\b",  RegexOptions.Compiled);
        private static readonly Regex BicRegex  =
            new Regex(@"\bBuiltInCategory\.([A-Za-z_][A-Za-z0-9_]*)\b",   RegexOptions.Compiled);
        private static readonly Regex UnitRegex =
            new Regex(@"\bUnitTypeId\.([A-Za-z_][A-Za-z0-9_]*)\b",        RegexOptions.Compiled);

        private readonly HashSet<string> _validBip;
        private readonly HashSet<string> _validBic;
        private readonly HashSet<string> _validUnit;

        private RevitEnumValidator()
        {
            _validBip  = BuildEnumSet("Autodesk.Revit.DB.BuiltInParameter");
            _validBic  = BuildEnumSet("Autodesk.Revit.DB.BuiltInCategory");
            _validUnit = BuildEnumSet("Autodesk.Revit.DB.UnitTypeId");
        }

        public ValidationResult Validate(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return ValidationResult.Ok();

            // If RevitAPI is not loaded we have no reference sets — skip validation.
            if (_validBip.Count == 0 && _validBic.Count == 0 && _validUnit.Count == 0)
                return ValidationResult.Ok();

            var issues = new List<ValidationIssue>();
            CheckMatches(code, BipRegex,  "BuiltInParameter", _validBip,  issues);
            CheckMatches(code, BicRegex,  "BuiltInCategory",  _validBic,  issues);
            CheckMatches(code, UnitRegex, "UnitTypeId",       _validUnit, issues);

            return new ValidationResult
            {
                IsValid = issues.Count == 0,
                Issues  = issues
            };
        }

        private static void CheckMatches(
            string code,
            Regex regex,
            string category,
            HashSet<string> valid,
            List<ValidationIssue> issues)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in regex.Matches(code))
            {
                string value = m.Groups[1].Value;
                if (!seen.Add(value)) continue;           // already reported
                if (valid.Contains(value))  continue;     // valid

                issues.Add(new ValidationIssue
                {
                    Severity     = "Error",
                    Category     = category,
                    InvalidValue = value,
                    Message      = $"{category}.{value} does not exist in this Revit installation."
                });
            }
        }

        private static HashSet<string> BuildEnumSet(string fullTypeName)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                // Try direct Type.GetType first (works if assembly is in GAC or same AppDomain)
                Type? t = Type.GetType(fullTypeName);

                // Fall back to scanning loaded assemblies
                if (t == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            t = asm.GetType(fullTypeName);
                            if (t != null) break;
                        }
                        catch { }
                    }
                }

                if (t == null || !t.IsEnum) return set;

                foreach (string name in Enum.GetNames(t))
                    set.Add(name);
            }
            catch { }
            return set;
        }
    }
}
