namespace CodeSwitchX.Voice.Audio;

public enum MicrophoneFailureKind
{
    Denied,
    Missing,
    Unavailable,
}

public sealed class MicrophoneException(MicrophoneFailureKind kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public MicrophoneFailureKind Kind { get; } = kind;
}

public static class MicrophoneFailure
{
    private const int AccessDenied = unchecked((int)0x80070005);
    private const int DeviceInvalidated = unchecked((int)0x88890004);
    private const int NotFound = unchecked((int)0x80070490);
    private const int EndpointNotFound = unchecked((int)0x88890005);

    public static MicrophoneFailureKind Classify(Exception error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            if (e is UnauthorizedAccessException)
            {
                return MicrophoneFailureKind.Denied;
            }

            switch (e.HResult)
            {
                case AccessDenied:
                    return MicrophoneFailureKind.Denied;
                case DeviceInvalidated:
                case NotFound:
                case EndpointNotFound:
                    return MicrophoneFailureKind.Missing;
            }
        }

        return MicrophoneFailureKind.Unavailable;
    }
}
