namespace Kubism.Core.Api;

/// <summary>An API server error. The server message is shown verbatim to the user.</summary>
public class KubeApiException(int statusCode, string? reason, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public int StatusCode { get; } = statusCode;
    public string? Reason { get; } = reason;
    public bool IsConflict => StatusCode == 409;
    public bool IsGone => StatusCode == 410;
    public bool IsForbidden => StatusCode == 403;
    public bool IsUnauthorized => StatusCode == 401;
    public bool IsNotFound => StatusCode == 404;
}

/// <summary>Raised when a connection can't be established (unreachable, auth plugin missing, TLS…).</summary>
public class KubeConnectionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Raised by <see cref="Actions.SafetyPolicy"/> when an action is refused.</summary>
public class SafetyException(string message) : Exception(message);
