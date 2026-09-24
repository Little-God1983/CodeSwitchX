using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace CodeSwitchX.Hosting.Win32;

/// <summary>Live DWM preview of a source window drawn inside a destination window's client area.</summary>
public sealed class DwmThumbnail : IDisposable
{
    private nint _id;

    private DwmThumbnail(nint id)
    {
        _id = id;
    }

    public static DwmThumbnail? TryRegister(nint destination, nint source)
    {
        var hr = PInvoke.DwmRegisterThumbnail(new HWND(destination), new HWND(source), out var id);
        return hr.Succeeded ? new DwmThumbnail(id) : null;
    }

    public void Update(ScreenRect destinationClientRect, byte opacity, bool visible)
    {
        if (_id == 0)
        {
            return;
        }

        var properties = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = PInvoke.DWM_TNP_RECTDESTINATION | PInvoke.DWM_TNP_VISIBLE | PInvoke.DWM_TNP_OPACITY | PInvoke.DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = new RECT
            {
                left = destinationClientRect.Left, top = destinationClientRect.Top,
                right = destinationClientRect.Right, bottom = destinationClientRect.Bottom,
            },
            fVisible = visible,
            opacity = opacity,
            fSourceClientAreaOnly = true,
        };
        PInvoke.DwmUpdateThumbnailProperties(_id, in properties);
    }

    public void Dispose()
    {
        if (_id != 0)
        {
            PInvoke.DwmUnregisterThumbnail(_id);
            _id = 0;
        }
    }
}
