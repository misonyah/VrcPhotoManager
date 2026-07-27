using System.Windows;

namespace VrcdnManager.Views;

/// <summary>
/// Embeds the Patreon-gated panel.vrcdn.live login inside the app. Once the user
/// finishes logging in (redirected back to the panel), the PHPSESSID cookie is read
/// directly from WebView2's CookieManager - no external browser/DevTools dependency.
/// </summary>
public partial class LoginWindow : Window
{
    public string? SessionCookie { get; private set; }

    public LoginWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await Browser.EnsureCoreWebView2Async();
        Browser.CoreWebView2.SourceChanged += async (_, _) => await CheckForSessionCookieAsync();
        Browser.CoreWebView2.Navigate("https://panel.vrcdn.live/obj-upload.php");
    }

    private async Task CheckForSessionCookieAsync()
    {
        var cookies = await Browser.CoreWebView2.CookieManager.GetCookiesAsync("https://panel.vrcdn.live");
        var sessionCookie = cookies.FirstOrDefault(c => c.Name == "PHPSESSID");
        if (sessionCookie is null) return;

        // Only treat it as "logged in" once we've actually navigated past the login page.
        if (Browser.CoreWebView2.Source.Contains("/login/", StringComparison.OrdinalIgnoreCase)) return;

        SessionCookie = sessionCookie.Value;
        DialogResult = true;
        Close();
    }
}
