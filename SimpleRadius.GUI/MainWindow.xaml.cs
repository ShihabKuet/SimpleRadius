using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SimpleRadius.Core;
using SimpleRadius.Core.Models;
using SimpleRadius.Core.Server;

namespace SimpleRadius.GUI;

// ── View model for the auth events grid ──────────────────────────────────────
public sealed class AuthEventRow
{
    public DateTime Timestamp  { get; init; }
    public string   Username   { get; init; } = "";
    public string   NasIp      { get; init; } = "";
    public string   Method     { get; init; } = "";
    public bool     Accepted   { get; init; }
    public string   ResultText => Accepted ? "Accept" : "Reject";
}

// ── Main window ───────────────────────────────────────────────────────────────
public partial class MainWindow : Window
{
    // ── Server ────────────────────────────────────────────────────────────────
    private RadiusServer?  _server;
    private DispatcherTimer _statsTimer = new();

    // ── Observable collections (bound to DataGrids) ───────────────────────────
    private readonly ObservableCollection<AuthEventRow> _authEvents = new();
    private readonly ObservableCollection<UserEntry>    _users      = new();
    private readonly ObservableCollection<NasClient>    _nasClients = new();

    // ── Current nav page ──────────────────────────────────────────────────────
    private string _currentPage = "Dashboard";

    // ── Nav button registry (set in ctor so XAML Name bindings exist) ─────────
    private Dictionary<string, Button>  _navButtons  = new();
    private Dictionary<string, UIElement> _pages     = new();

    public MainWindow()
    {
        InitializeComponent();
        SetupNavigation();
        SetupGridBindings();
        SetupStatsTimer();
        NavigateTo("Dashboard");
        UpdateServerStatus(running: false);
    }

    // ── Navigation ────────────────────────────────────────────────────────────
    private void SetupNavigation()
    {
        _navButtons = new()
        {
            ["Dashboard"] = NavDashboard,
            ["Logs"]      = NavLogs,
            ["Users"]     = NavUsers,
            ["NAS"]       = NavNas,
            ["Settings"]  = NavSettings,
        };

        _pages = new()
        {
            ["Dashboard"] = PageDashboard,
            ["Logs"]      = PageLogs,
            ["Users"]     = PageUsers,
            ["NAS"]       = PageNas,
            ["Settings"]  = PageSettings,
        };
    }

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string page)
            NavigateTo(page);
    }

    private void NavigateTo(string page)
    {
        // Hide all pages
        foreach (var p in _pages.Values) p.Visibility = Visibility.Collapsed;

        // Reset all nav button styles
        foreach (var b in _navButtons.Values)
            b.Style = (Style)FindResource("NavButtonStyle");

        // Show target page and activate nav button
        if (_pages.TryGetValue(page, out var target))
            target.Visibility = Visibility.Visible;

        if (_navButtons.TryGetValue(page, out var navBtn))
            navBtn.Style = (Style)FindResource("NavButtonActiveStyle");

        _currentPage = page;

        var pageMeta = page switch
        {
            "Dashboard" => ("Dashboard",   "Server overview and live statistics"),
            "Logs"      => ("Live Logs",   "Real-time RADIUS server log stream"),
            "Users"     => ("Users",       "Manage local user accounts"),
            "NAS"       => ("NAS / Clients","Configure network access servers and shared secrets"),
            "Settings"  => ("Settings",    "Server configuration"),
            _           => (page, "")
        };
        PageTitle.Text    = pageMeta.Item1;
        PageSubtitle.Text = pageMeta.Item2;
    }

    // ── DataGrid bindings ─────────────────────────────────────────────────────
    private void SetupGridBindings()
    {
        DashboardEventGrid.ItemsSource = _authEvents;
        UsersGrid.ItemsSource          = _users;
        NasGrid.ItemsSource            = _nasClients;
    }

    // ── Stats refresh timer ───────────────────────────────────────────────────
    private void SetupStatsTimer()
    {
        _statsTimer.Interval = TimeSpan.FromSeconds(1);
        _statsTimer.Tick    += StatsTimer_Tick;
    }

    private void StatsTimer_Tick(object? sender, EventArgs e)
    {
        if (_server == null) return;

        StatAccepts.Text    = _server.TotalAccepts.ToString("N0");
        StatRejects.Text    = _server.TotalRejects.ToString("N0");
        StatAccounting.Text = _server.TotalAccounting.ToString("N0");

        var up = _server.Uptime;
        StatUptime.Text = up.TotalHours >= 1
            ? $"{(int)up.TotalHours}h {up.Minutes:D2}m"
            : $"{up.Minutes}m {up.Seconds:D2}s";

        UptimeText.Text = $"Uptime: {StatUptime.Text}";
    }

    // ── Server start / stop ───────────────────────────────────────────────────
    private void BtnToggleServer_Click(object sender, RoutedEventArgs e)
    {
        if (_server == null || !_server.IsRunning)
            StartServer();
        else
            StopServer();
    }

    private void StartServer()
    {
        try
        {
            // Build config from settings fields
            var config = new RadiusServerConfig
            {
                AuthPort    = int.TryParse(TxtAuthPort.Text, out int ap) ? ap : 1812,
                AcctPort    = int.TryParse(TxtAcctPort.Text, out int cp) ? cp : 1813,
                BindAddress = string.IsNullOrWhiteSpace(TxtBindAddr.Text) ? "0.0.0.0" : TxtBindAddr.Text.Trim(),
                DataDir     = string.IsNullOrWhiteSpace(TxtDataDir.Text)  ? "data"    : TxtDataDir.Text.Trim(),
            };

            // GUI-bridging logger: routes Core log lines into the live log listbox
            var guiLogger = new GuiRadiusLogger(msg =>
                Dispatcher.InvokeAsync(() => AppendLog(msg)));

            _server = new RadiusServer(config, guiLogger);

            // Wire server events → UI (always dispatch to UI thread)
            _server.OnLog += (_, msg) =>
                Dispatcher.InvokeAsync(() => AppendLog(msg));

            _server.OnAuthEvent += (_, ev) =>
                Dispatcher.InvokeAsync(() =>
                {
                    _authEvents.Insert(0, new AuthEventRow
                    {
                        Timestamp = ev.Timestamp,
                        Username  = ev.Username,
                        NasIp     = ev.NasIp,
                        Method    = ev.Method,
                        Accepted  = ev.Accepted,
                    });

                    // Keep the dashboard list manageable
                    while (_authEvents.Count > 200)
                        _authEvents.RemoveAt(_authEvents.Count - 1);
                });

            _server.Start();

            // Populate grids from the now-loaded stores
            RefreshUsersGrid();
            RefreshNasGrid();

            _statsTimer.Start();
            UpdateServerStatus(running: true);
            AppendLog($"[INFO] Server started on ports {config.AuthPort}/{config.AcctPort}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to start server:\n\n{ex.Message}",
                "Simple Radius — Start Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StopServer()
    {
        _statsTimer.Stop();
        _server?.Stop();
        UpdateServerStatus(running: false);
        AppendLog("[INFO] Server stopped.");
    }

    private void UpdateServerStatus(bool running)
    {
        if (running)
        {
            StatusDotColor.Color  = (System.Windows.Media.Color)FindResource("AccentGreen");
            StatusTextColor.Color = (System.Windows.Media.Color)FindResource("AccentGreen");
            StatusChipBg.Color    = System.Windows.Media.Color.FromRgb(0x0D, 0x26, 0x1E);
            StatusText.Text       = "Server Running";
            BtnToggleServer.Style = (Style)FindResource("BtnDanger");
            BtnToggleServer.Content = "Stop Server";

            StatUptime.Text = "0m 00s";
        }
        else
        {
            StatusDotColor.Color  = (System.Windows.Media.Color)FindResource("AccentRed");
            StatusTextColor.Color = (System.Windows.Media.Color)FindResource("AccentRed");
            StatusChipBg.Color    = System.Windows.Media.Color.FromRgb(0x26, 0x0D, 0x0D);
            StatusText.Text       = "Server Stopped";
            BtnToggleServer.Style = (Style)FindResource("BtnPrimary");
            BtnToggleServer.Content = "Start Server";

            StatUptime.Text     = "—";
            UptimeText.Text     = "Uptime: —";
        }
    }

    // ── Live log ──────────────────────────────────────────────────────────────
    private void AppendLog(string message)
    {
        LogListBox.Items.Add(message);

        // Trim log to 2000 lines to avoid memory bloat
        while (LogListBox.Items.Count > 2000)
            LogListBox.Items.RemoveAt(0);

        if (ChkAutoScroll.IsChecked == true && LogListBox.Items.Count > 0)
            LogListBox.ScrollIntoView(LogListBox.Items[^1]);
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        => LogListBox.Items.Clear();

    // ── Users management ──────────────────────────────────────────────────────
    private void RefreshUsersGrid()
    {
        if (_server == null) return;
        _users.Clear();
        foreach (var u in _server.Users.GetAll())
            _users.Add(u);
    }

    private void BtnAddUser_Click(object sender, RoutedEventArgs e)
    {
        UserFormError.Visibility = Visibility.Collapsed;

        if (_server == null)
        {
            ShowUserError("Start the server first.");
            return;
        }

        string username = TxtNewUsername.Text.Trim();
        string password = PwdNewPassword.Password;
        string group    = (CmbUserGroup.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "default";
        string vlanStr  = TxtNewVlan.Text.Trim();

        if (string.IsNullOrEmpty(username))  { ShowUserError("Username is required."); return; }
        if (string.IsNullOrEmpty(password))  { ShowUserError("Password is required."); return; }
        if (!int.TryParse(vlanStr, out int vlanId) || vlanId < 0 || vlanId > 4094)
        { ShowUserError("VLAN ID must be 0–4094."); return; }

        var user = new UserEntry
        {
            Username  = username,
            Password  = password,
            Group     = group,
            VlanId    = vlanId,
            IsEnabled = true,
        };

        if (!_server.Users.Add(user))
        {
            ShowUserError($"User '{username}' already exists.");
            return;
        }

        // Clear form
        TxtNewUsername.Text   = "";
        PwdNewPassword.Password = "";
        TxtNewVlan.Text       = "1";

        RefreshUsersGrid();
        AppendLog($"[INFO] User added: {username} (Group={group}, VLAN={vlanId})");
    }

    private void BtnDeleteUser_Click(object sender, RoutedEventArgs e)
    {
        if (_server == null || UsersGrid.SelectedItem is not UserEntry user) return;

        var result = MessageBox.Show(
            $"Delete user '{user.Username}'?",
            "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        _server.Users.Remove(user.Username);
        RefreshUsersGrid();
        AppendLog($"[INFO] User deleted: {user.Username}");
    }

    private void BtnToggleUser_Click(object sender, RoutedEventArgs e)
    {
        if (_server == null || UsersGrid.SelectedItem is not UserEntry user) return;

        var updated = new UserEntry
        {
            Username              = user.Username,
            Password              = user.Password,
            Group                 = user.Group,
            VlanId                = user.VlanId,
            IsEnabled             = !user.IsEnabled,
            Description           = user.Description,
            SessionTimeoutSeconds = user.SessionTimeoutSeconds,
        };

        _server.Users.Update(updated);
        RefreshUsersGrid();
        AppendLog($"[INFO] User '{user.Username}' → Enabled={updated.IsEnabled}");
    }

    private void UsersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void ShowUserError(string msg)
    {
        UserFormError.Text       = msg;
        UserFormError.Visibility = Visibility.Visible;
    }

    // ── NAS management ────────────────────────────────────────────────────────
    private void RefreshNasGrid()
    {
        if (_server == null) return;
        _nasClients.Clear();
        foreach (var n in _server.Nas.GetAll())
            _nasClients.Add(n);
    }

    private void BtnAddNas_Click(object sender, RoutedEventArgs e)
    {
        NasFormError.Visibility = Visibility.Collapsed;

        if (_server == null)
        {
            ShowNasError("Start the server first.");
            return;
        }

        string name   = TxtNasName.Text.Trim();
        string ip     = TxtNasIp.Text.Trim();
        string secret = PwdNasSecret.Password;
        string vendor = (CmbNasVendor.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Generic";

        if (string.IsNullOrEmpty(name))   { ShowNasError("Name is required."); return; }
        if (string.IsNullOrEmpty(ip))     { ShowNasError("IP Address is required."); return; }
        if (string.IsNullOrEmpty(secret)) { ShowNasError("Shared secret is required."); return; }
        if (secret.Length < 8)            { ShowNasError("Shared secret should be at least 8 characters."); return; }

        var nas = new NasClient
        {
            Name         = name,
            IpAddress    = ip,
            SharedSecret = secret,
            Vendor       = vendor,
        };

        if (!_server.Nas.Add(nas))
        {
            ShowNasError($"A NAS named '{name}' already exists.");
            return;
        }

        TxtNasName.Text      = "";
        TxtNasIp.Text        = "";
        PwdNasSecret.Password = "";

        RefreshNasGrid();
        AppendLog($"[INFO] NAS added: {name} ({ip}) [{vendor}]");
    }

    private void BtnDeleteNas_Click(object sender, RoutedEventArgs e)
    {
        if (_server == null || NasGrid.SelectedItem is not NasClient nas) return;

        var result = MessageBox.Show(
            $"Delete NAS '{nas.Name}'?",
            "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        _server.Nas.Remove(nas.Name);
        RefreshNasGrid();
        AppendLog($"[INFO] NAS deleted: {nas.Name}");
    }

    private void NasGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void ShowNasError(string msg)
    {
        NasFormError.Text       = msg;
        NasFormError.Visibility = Visibility.Visible;
    }

    // ── Window closing ────────────────────────────────────────────────────────
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_server?.IsRunning == true)
        {
            var result = MessageBox.Show(
                "The RADIUS server is still running.\nStop it and exit?",
                "Simple Radius", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.No)
            {
                e.Cancel = true;
                return;
            }
        }

        _statsTimer.Stop();
        _server?.Dispose();
        base.OnClosing(e);
    }
}

// ── IRadiusLogger implementation that bridges Core logs into the WPF GUI ──────
internal sealed class GuiRadiusLogger : IRadiusLogger
{
    private readonly Action<string> _append;
    public GuiRadiusLogger(Action<string> append) => _append = append;

    public void Info(string message)  => _append($"[{Now}] [INF] {message}");
    public void Warn(string message)  => _append($"[{Now}] [WRN] {message}");
    public void Error(string message, Exception? ex = null)
        => _append($"[{Now}] [ERR] {message}{(ex != null ? $" — {ex.Message}" : "")}");

    private static string Now => DateTime.Now.ToString("HH:mm:ss.fff");
}
