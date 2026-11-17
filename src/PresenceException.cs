namespace Trysac.SignalrPresenceHub;

/// <summary>Exception thrown by the presence hub when a validation, capacity or state invariant is violated.</summary>
public sealed class PresenceException : ArgumentException
{
    /// <summary>Default machine-readable code when constructed without a specific code.</summary>
    public const string DefaultCode = "invalid_name";

    /// <summary>Stable machine-readable error code (<c>duplicate_connection</c>, <c>unknown_connection</c>, <c>invalid_name</c>, <c>too_many_rooms</c>, <c>capacity</c>, <c>not_started</c>).</summary>
    public string Code { get; }

    /// <summary>Creates a new <see cref="PresenceException"/> with the default code and message.</summary>
    public PresenceException()
        : base("Presence operation failed.")
    {
        Code = DefaultCode;
    }

    /// <summary>Creates a new <see cref="PresenceException"/> with a code and a short message.</summary>
    /// <param name="code">Machine-readable error code.</param>
    /// <param name="message">Short human-readable message without secrets.</param>
    public PresenceException(string code, string message)
        : base(TruncateMessage(message))
    {
        ArgumentNullException.ThrowIfNull(code);
        Code = code;
    }

    /// <summary>Creates a new <see cref="PresenceException"/> with a message using the default error code.</summary>
    /// <param name="message">Short human-readable message.</param>
    public PresenceException(string message)
        : base(TruncateMessage(message))
    {
        Code = DefaultCode;
    }

    /// <summary>Creates a new <see cref="PresenceException"/> with a message and inner exception.</summary>
    /// <param name="message">Short human-readable message.</param>
    /// <param name="innerException">Underlying cause.</param>
    public PresenceException(string message, Exception innerException)
        : base(TruncateMessage(message), innerException)
    {
        Code = DefaultCode;
    }

    /// <summary>Creates a new <see cref="PresenceException"/> with a code, message and inner exception.</summary>
    /// <param name="code">Machine-readable error code.</param>
    /// <param name="message">Short human-readable message.</param>
    /// <param name="innerException">Underlying cause.</param>
    public PresenceException(string code, string message, Exception innerException)
        : base(TruncateMessage(message), innerException)
    {
        ArgumentNullException.ThrowIfNull(code);
        Code = code;
    }

    private static string TruncateMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return "Presence operation failed.";
        }

        return message.Length <= 200 ? message : message[..200];
    }
}
