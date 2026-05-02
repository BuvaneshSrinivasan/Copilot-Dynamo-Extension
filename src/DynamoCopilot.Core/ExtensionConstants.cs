namespace DynamoCopilot.Core
{
    // =========================================================================
    // ExtensionConstants — identifiers shared across both Dynamo extensions
    // =========================================================================
    //
    // Extension IDs must match the values used server-side in AppConstants and
    // in the JWT "ext" claims. Change them here and on the server together.
    // =========================================================================

    public static class ExtensionConstants
    {
        public const string CopilotId      = "Copilot";
        public const string SuggestNodesId = "SuggestNodes";

        // Contact address shown in the "no licence" notice.
        public const string SupportEmail = "info@bimera.com";
    }
}
