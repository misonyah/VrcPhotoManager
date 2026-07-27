using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;

namespace VrcdnManager.Views;

/// <summary>
/// Embeds the Patreon-gated panel.vrcdn.live login inside the app. Once the user
/// finishes logging in, the PHPSESSID cookie is read directly from WebView2's
/// CookieManager - no external browser/DevTools dependency.
///
/// Bug fixed 2026-07-27: originally guessed "logged in" from the page URL not
/// containing "/login/". That never matched the real redirect path
/// (id.vrcdn.live/Account/Login?ReturnUrl=...), and PHP sets a PHPSESSID cookie on
/// the very first unauthenticated request anyway - so the dialog closed almost
/// immediately with a bogus, unauthenticated cookie, before the user could actually
/// log in. Fixed by testing the cookie against the real API (getQuota) instead of
/// guessing from the URL - only treat it as a real session once the API actually
/// accepts it.
/// </summary>
public partial class LoginWindow : Window
{
    public string? SessionCookie { get; private set; }

    private bool _checking;

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

        Browser.CoreWebView2.SourceChanged += async (_, _) => await CheckForSessionCookieAsync();
        Browser.CoreWebView2.Navigate("https://panel.vrcdn.live/obj-upload.php");
    }

    private async Task CheckForSessionCookieAsync()
    {
        if (_checking) return; // SourceChanged fires repeatedly during the OAuth redirect chain
        _checking = true;
        try
        {
            var cookies = await Browser.CoreWebView2.CookieManager.GetCookiesAsync("https://panel.vrcdn.live");
            var sessionCookie = cookies.FirstOrDefault(c => c.Name == "PHPSESSID");
            if (sessionCookie is null) return;

            if (!await IsAuthenticatedAsync(sessionCookie.Value)) return;

            SessionCookie = sessionCookie.Value;
            DialogResult = true;
            Close();
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>
    /// The only reliable way to know the cookie is a real, logged-in session: ask the
    /// API something that requires auth (getQuota) and see if it actually answers,
    /// rather than inferring from the page URL.
    /// </summary>
    private static async Task<bool> IsAuthenticatedAsync(string cookieValue)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("Cookie", $"PHPSESSID={cookieValue}");
            http.DefaultRequestHeaders.Add("Referer", "https://panel.vrcdn.live/obj-upload.php");
            using var resp = await http.PostAsJsonAsync(
                "https://panel.vrcdn.live/parts/s3.funcs.php", new { type = "getQuota" });
            if (!resp.IsSuccessStatusCode) return false;
            if (resp.Content.Headers.ContentType?.MediaType != "application/json") return false;

            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("QuotaUsed", out _);
        }
        catch
        {
            return false;
        }
    }
}
