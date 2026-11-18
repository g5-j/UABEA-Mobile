using Android.App;

namespace UABEA.OriginalHost;

[Application]
public sealed class HostApplication : Application
{
    public HostApplication(IntPtr handle, Android.Runtime.JniHandleOwnership ownership)
        : base(handle, ownership) { }
}
