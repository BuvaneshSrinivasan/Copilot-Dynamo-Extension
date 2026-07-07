using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace DynamoCopilot.Core.Services.Validation
{
    /// <summary>
    /// Validates generated Python code against the real static-helper surface of the
    /// installed Revit API (e.g. <c>UnitUtils</c>, <c>LabelUtils</c>, <c>WallUtils</c>),
    /// catching invented methods/properties that <see cref="RevitEnumValidator"/> does not
    /// cover (that class only checks enum-style constants such as BuiltInParameter).
    ///
    /// Scope is deliberately limited to "ClassName.Member" references where ClassName is a
    /// C# static class (IsAbstract &amp; IsSealed) reflected from the loaded RevitAPI /
    /// RevitAPIUI / RevitAPIIFC assemblies. Static classes can never be instance variables,
    /// so this check cannot collide with local Python variable names — the same reason
    /// RevitEnumValidator's regex approach is safe. Instance-member calls (e.g. wall.Location)
    /// are intentionally not checked: without real Python type inference, flagging those would
    /// produce false positives.
    /// Silently skips validation if the assembly is not present (e.g. unit tests, standalone).
    /// </summary>
    public sealed class RevitApiSurfaceValidator
    {
        public static readonly RevitApiSurfaceValidator Instance = new RevitApiSurfaceValidator();

        private static readonly string[] AssemblyNames =
        {
            "RevitAPI",
            "RevitAPIUI",
            "RevitAPIIFC",
        };

        private static readonly Regex MemberAccessRegex =
            new Regex(@"\b([A-Z][A-Za-z0-9]*)\.([A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.Compiled);

        private readonly Dictionary<string, HashSet<string>> _membersByType;

        private RevitApiSurfaceValidator()
        {
            _membersByType = BuildStaticClassIndex();
        }

        public ValidationResult Validate(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return ValidationResult.Ok();

            // If the RevitAPI assemblies are not loaded we have no reference set — skip validation.
            if (_membersByType.Count == 0) return ValidationResult.Ok();

            var issues = new List<ValidationIssue>();
            var seen   = new HashSet<string>(StringComparer.Ordinal);

            foreach (Match m in MemberAccessRegex.Matches(code))
            {
                string className  = m.Groups[1].Value;
                string memberName = m.Groups[2].Value;

                if (!_membersByType.TryGetValue(className, out var members))
                    continue; // not a known Revit static-helper class — not our concern

                string key = className + "." + memberName;
                if (!seen.Add(key)) continue; // already reported
                if (members.Contains(memberName)) continue; // valid

                issues.Add(new ValidationIssue
                {
                    Severity     = "Error",
                    Category     = "ApiMember",
                    InvalidValue = key,
                    Message      = $"{key} does not exist on {className} in this Revit installation."
                });
            }

            return new ValidationResult
            {
                IsValid = issues.Count == 0,
                Issues  = issues
            };
        }

        private static Dictionary<string, HashSet<string>> BuildStaticClassIndex()
        {
            var index = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            try
            {
                var revitAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => AssemblyNames.Contains(a.GetName().Name, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                foreach (var asm in revitAssemblies)
                {
                    Type[] types;
                    try { types = asm.GetExportedTypes(); }
                    catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
                    catch { continue; }

                    foreach (var type in types)
                    {
                        if (type == null) continue;
                        if (type.Namespace == null || !type.Namespace.StartsWith("Autodesk.Revit", StringComparison.Ordinal))
                            continue;
                        // A C# "static class" compiles to abstract + sealed with no instance members.
                        if (!type.IsAbstract || !type.IsSealed || type.IsEnum || type.IsInterface)
                            continue;

                        var members = new HashSet<string>(StringComparer.Ordinal);

                        foreach (var mi in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                        {
                            if (mi.IsSpecialName) continue; // skip property get_/set_ accessors, already covered below
                            members.Add(mi.Name);
                        }
                        foreach (var pi in type.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                            members.Add(pi.Name);
                        foreach (var fi in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                            members.Add(fi.Name);

                        if (members.Count == 0) continue;

                        if (index.TryGetValue(type.Name, out var existing))
                            existing.UnionWith(members); // rare simple-name collision across namespaces — merge, don't drop either
                        else
                            index[type.Name] = members;
                    }
                }
            }
            catch { }
            return index;
        }
    }
}
