using System.Windows;

namespace VrcPhotoManager.Views;

/// <summary>
/// Embeds the Patreon-gated panel.vrcdn.live login inside the app. Once the user
/// finishes logging in, the PHPSESSID cookie is read directly from WebView2's
/// CookieManager - no external browser/DevTools dependency.
///
/// Bug history (2026-07-27):
/// 1. Originally guessed "logged in" from the page URL not containing "/login/". That
///    never matched the real redirect path (id.vrcdn.live/Account/Login?ReturnUrl=...),
///    so the dialog closed almost instantly with a bogus, unauthenticated cookie.
/// 2. Fixed by probing the real API (getQuota) on every SourceChanged - but this made an
///    out-of-band HTTP request to panel.vrcdn.live WHILE its own OAuth callback
///    (login/return.php) was mid-flight, validating its `state` against a PHP session.
///    Confirmed via live WebView2 remote-debugging (chrome-devtools MCP attached to
///    --remote-debugging-port) that this raced panel.vrcdn.live's own session handling:
///    the OIDC handshake with id.vrcdn.live/Patreon completed successfully every time
///    (valid code+state came back), but panel's own return.php intermittently rendered
///    "Error: Unable to determine state" anyway - and manually re-navigating to the
///    target page immediately afterward showed the session was ALREADY valid regardless
///    of that error page. So: stop making any extra request mid-flow, and treat the
///    error page as a transient hiccup worth one silent retry rather than a real failure.
/// 3. SourceChanged fires on URL change, before the new page finishes loading - checking
///    document.body.innerText right away can read an empty/partial DOM and miss the
///    "Logout" marker entirely, with no second chance since the URL won't change again.
///    Fixed by using NavigationCompleted instead, which fires once the page has actually
///    finished loading.
/// </summary>
public partial class LoginWindow : Window
{
    private const string TargetUrl = "https://panel.vrcdn.live/obj-upload.php";

    public string? SessionCookie { get; private set; }

    private bool _checking;
    private bool _retried;

    public LoginWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await Browser.EnsureCoreWebView2Async();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Couldn't start the embedded browser (WebView2 Runtime may be missing):\n{ex.Message}",
                "Login failed", MessageBoxButton.OK, MessageBoxImage.Error);
            DialogResult = false;
            Close();
            return;
        }

        Browser.CoreWebView2.NavigationCompleted += async (_, _) => await OnNavigationCompletedAsync();
        Browser.CoreWebView2.Navigate(TargetUrl);
    }

    private async Task OnNavigationCompletedAsync()
    {
        if (_checking) return; // fires repeatedly during the OAuth redirect chain
        _checking = true;
        try
        {
            string currentUrl = Browser.CoreWebView2.Source;
            if (!currentUrl.StartsWith("https://panel.vrcdn.live", StringComparison.OrdinalIgnoreCase))
                return; // still mid-flow through id.vrcdn.live / Patreon

            string pageText = await Browser.CoreWebView2.ExecuteScriptAsync("document.body.innerText");

            if (pageText.Contains("Unable to determine state", StringComparison.OrdinalIgnoreCase))
            {
                // Confirmed (via live devtools) this is a transient hiccup in panel's own
                // callback handler, not an actual failed login - the underlying session is
                // already valid by this point. One silent retry resolves it.
                if (!_retried)
                {
                    _retried = true;
                    Browser.CoreWebView2.Navigate(TargetUrl);
                }
                return;
            }

            if (pageText.Contains("Logout", StringComparison.OrdinalIgnoreCase))
            {
                var cookies = await Browser.CoreWebView2.CookieManager.GetCookiesAsync("https://panel.vrcdn.live");
                var sessionCookie = cookies.FirstOrDefault(c => c.Name == "PHPSESSID");
                if (sessionCookie is null) return;

                SessionCookie = sessionCookie.Value;
                DialogResult = true;
                Close();
            }
        }
        finally
        {
            _checking = false;
        }
    }
}
