namespace MyLocalAssistant.Shared.Contracts;

/// <summary>
/// Stable string codes returned in problem-details responses.
/// Clients can use these for branching without parsing prose.
/// </summary>
public static class ProblemCodes
{
    public const string InvalidCredentials = "auth.invalid_credentials";
    public const string MustChangePassword = "auth.must_change_password";
    public const string TokenExpired = "auth.token_expired";
    public const string Forbidden = "auth.forbidden";
    public const string NotFound = "common.not_found";
    public const string ValidationFailed = "common.validation_failed";
    public const string ModelNotLoaded = "llm.model_not_loaded";
    public const string ModelBusy = "llm.model_busy";
}
