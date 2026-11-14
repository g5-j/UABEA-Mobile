using Android.App;
using Android.Content.PM;
using Avalonia.Android;

namespace UABEA.Mobile;

[Activity(
    Label = "UABE Avalonia",
    Theme = "@style/UABEAStartingTheme",
    MainLauncher = true,
    Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation |
                           ConfigChanges.ScreenSize |
                           ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity<App>
{
    public override void OnBackPressed()
    {
        if (App.CurrentMainView?.HandleSystemBack() == true)
            return;

        base.OnBackPressed();
    }
}