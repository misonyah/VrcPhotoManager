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
///
/// Silent mode (2026-08-12): WebView2 keeps its own persistent browser profile (cookies,
/// including the Patreon OAuth session, survive across app restarts) separately from the
/// PHPSESSID this class extracts - so a PHPSESSID that's expired doesn't mean the
/// underlying Patreon login has expired too, usually a much longer-lived session. Re-running
/// the exact same navigation in an off-screen window lets that stale Patreon session silently
/// complete the OAuth redirect chain and mint a fresh PHPSESSID with no user interaction at
/// all - see TrySilentLoginAsync. Only falls back to an actual visible login when the Patreon
/// session itself is also gone (the flow lands on a real login form and just sits there,
/// caught by the timeout in TrySilentLoginAsync).
/// </summary>
public partial class LoginWindow : Window
{
    private const string TargetUrl = "https://panel.vrcdn.live/obj-upload.php";

    public string? SessionCookie { get; private set; }

    private bool _checking;
    private bool _retried;
    private readonly bool _silent;
    private readonly TaskCompletionSource<string?>? _silentCompletion;

    public LoginWindow() : this(silent: false)
    {
    }

    private LoginWindow(bool silent)
    {
        InitializeComponent();
        _silent = silent;
        if (silent)
        {
            _silentCompletion = new TaskCompletionSource<string?>();
            // Off-screen rather than Visibility=Hidden - WebView2 needs a real, laid-out HWND
            // to initialize and navigate correctly, which a Hidden window doesn't reliably give
            // it. Placed far off any real desktop instead of relying on WindowStyle=None +
            // ShowInTaskbar=false alone to keep it from ever flashing on screen.
            WindowStyle = WindowStyle.None;
            ShowInTaskbar = false;
            ShowActivated = false;
            Width = 50;
            Height = 50;
            Left = -32000;
            Top = -32000;
        }
        Loaded += async (_, _) => await InitializeAsync();
    }

    /// <summary>Attempts to refresh the VRCDN session with no visible UI - see the class doc
    /// comment's "Silent mode" section. Returns null (never throws) if the underlying Patreon
    /// session is also gone, or on any other failure - the caller's only recourse at that point
    /// is an interactive LoginWindow.</summary>
    public static async Task<string?> TrySilentLoginAsync(TimeSpan? timeout = null)
    {
        var window = new LoginWindow(silent: true);
        window.Show();
        try
        {
            var completed = window._silentCompletion!.Task;
            var winner = await Task.WhenAny(completed, Task.Delay(timeout ?? TimeSpan.FromSeconds(20)));
            return winner == completed ? await completed : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            window.Close();
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            await Browser.EnsureCoreWebView2Async();
        }
        catch (Exception ex)
        {
            if (!_silent)
            {
                MessageBox.Show(this,
                    $"Couldn't start the embedded browser (WebView2 Runtime may be missing):\n{ex.Message}",
                    "Login failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Complete(success: false);
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
            // A real report: the redirect chain back from Patreon (after clicking Allow) can
            // sit for a noticeable while with the window looking completely idle - no title
            // change, no spinner, nothing to suggest it's still working rather than stuck. Set
            // as soon as any post-click navigation happens, not just once panel.vrcdn.live is
            // reached, since the Patreon-side leg of the redirect is the slow part being
            // reported. No-op for the silent/off-screen window (Title is never seen there).
            if (!_silent) Title = "Log in to VRCDN — finishing sign-in, please wait...";

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
                Complete(success: true);
            }
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>Routes "the login flow finished" (success or failure) through DialogResult/Close
    /// for the interactive case (LoginAsync's caller awaits ShowDialog()) or the completion
    /// source for the silent case (TrySilentLoginAsync's caller awaits that directly) - setting
    /// DialogResult on a window that was never shown via ShowDialog throws, so the two paths
    /// can't share the same call.</summary>
    private void Complete(bool success)
    {
        if (_silent)
        {
            _silentCompletion!.TrySetResult(success ? SessionCookie : null);
        }
        else
        {
            DialogResult = success;
            Close();
        }
    }
}
