namespace DynamoCopilot.Server;

public static class AppConstants
{
    // Extension identifiers — must match the "ext" JWT claim values and the
    // ExtensionConstants in DynamoCopilot.Core (client side).
    public static class Extensions
    {
        public const string Copilot      = "Copilot";
        public const string SuggestNodes = "SuggestNodes";
    }
}
