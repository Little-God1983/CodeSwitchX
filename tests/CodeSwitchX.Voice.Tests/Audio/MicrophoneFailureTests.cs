namespace CodeSwitchX.Voice.Tests.Audio;

using System.Runtime.InteropServices;
using CodeSwitchX.Voice.Audio;

public sealed class MicrophoneFailureTests
{
    [Theory]
    [InlineData(0x80070005u, MicrophoneFailureKind.Denied)]
    [InlineData(0x88890004u, MicrophoneFailureKind.Missing)]
    [InlineData(0x80070490u, MicrophoneFailureKind.Missing)]
    [InlineData(0x88890005u, MicrophoneFailureKind.Missing)]
    [InlineData(0x8889000Au, MicrophoneFailureKind.Unavailable)]
    [InlineData(0x80004005u, MicrophoneFailureKind.Unavailable)]
    public void A_com_error_is_classified_by_its_hresult(uint hresult, MicrophoneFailureKind expected)
    {
        MicrophoneFailure.Classify(new COMException("x", unchecked((int)hresult))).ShouldBe(expected);
    }

    [Fact]
    public void An_unauthorized_access_exception_means_denied()
    {
        MicrophoneFailure.Classify(new UnauthorizedAccessException()).ShouldBe(MicrophoneFailureKind.Denied);
    }

    [Fact]
    public void The_inner_exception_decides_when_the_outer_one_is_generic()
    {
        var inner = new COMException("x", unchecked((int)0x88890004));
        MicrophoneFailure.Classify(new InvalidOperationException("wrapped", inner)).ShouldBe(MicrophoneFailureKind.Missing);
    }

    [Fact]
    public void An_unrelated_exception_is_unavailable()
    {
        MicrophoneFailure.Classify(new InvalidOperationException("boom")).ShouldBe(MicrophoneFailureKind.Unavailable);
    }
}
