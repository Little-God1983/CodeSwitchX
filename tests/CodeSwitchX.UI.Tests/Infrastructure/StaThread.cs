using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace CodeSwitchX.UI.Tests.Infrastructure;

/// <summary>Runs a test body on its own STA thread, which WPF windows need, and sends it window messages.</summary>
public static class StaThread
{
    /// <summary>A body that blocks (a window that cannot be created on a locked desktop, say) fails its test instead of hanging the run.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    public static Task RunAsync(Action body)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                body();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        }) { IsBackground = true, Name = "test-sta" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    public static extern nint SendMessage(nint hwnd, int msg, nint wParam, nint lParam);
}
