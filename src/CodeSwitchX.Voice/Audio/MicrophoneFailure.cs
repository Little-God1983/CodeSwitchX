namespace CodeSwitchX.Voice.Audio;

public enum MicrophoneFailureKind
{
    Denied,
    Missing,
    Unavailable,

    /// <summary>The Windows Audio service is not running: no microphone can be opened until it is started.</summary>
    AudioServiceDown,
}

public sealed class MicrophoneException(MicrophoneFailureKind kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public MicrophoneFailureKind Kind { get; } = kind;
}

public static class MicrophoneFailure
{
    private const int AccessDenied = unchecked((int)0x80070005);
    private const int DeviceInvalidated = unchecked((int)0x88890004); // AUDCLNT_E_DEVICE_INVALIDATED
    private const int NotFound = unchecked((int)0x80070490); // E_NOTFOUND
    private const int EndpointCreateFailed = unchecked((int)0x8889000F); // AUDCLNT_E_ENDPOINT_CREATE_FAILED
    private const int ServiceNotRunning = unchecked((int)0x88890010); // AUDCLNT_E_SERVICE_NOT_RUNNING

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
                case EndpointCreateFailed:
                    return MicrophoneFailureKind.Missing;
                case ServiceNotRunning:
                    return MicrophoneFailureKind.AudioServiceDown;
            }
        }

        return MicrophoneFailureKind.Unavailable;
    }
}
