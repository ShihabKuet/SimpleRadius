using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SimpleRadius.Core;
using SimpleRadius.Core.Models;
using SimpleRadius.Core.Server;

namespace SimpleRadius.GUI;

// ── Auth event row (dashboard grid) ──────────────────────────────────────────
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
    private RadiusServer?   _server;
    private DispatcherTimer _statsTimer = new();

    // ── Grid data sources ─────────────────────────────────────────────────────
    private readonly ObservableCollection<AuthEventRow> _authEvents = new();
    private readonly ObservableCollection<UserEntry>    _users      = new();
    private readonly ObservableCollection<NasClient>    _nasClients = new();

    // ── Edit-mode state ───────────────────────────────────────────────────────
    private string? _editingUsername;   // null = Add mode, non-null = Edit mode
    private string? _editingNasName;

    // ── Nav registry ─────────────────────────────────────────────────────────
    private Dictionary<string, Button>    _navButtons = new();
    private Dictionary<string, UIElement> _pages      = new();
    private string _currentPage = "Dashboard";

    // ─────────────────────────────────────────────────────────────────────────
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
        foreach (var p in _pages.Values)      p.Visibility = Visibility.Collapsed;
        foreach (var b in _navButtons.Values) b.Style = (Style)FindResource("NavButtonStyle");

        if (_pages.TryGetValue(page, out var target))   target.Visibility = Visibility.Visible;
        if (_navButtons.TryGetValue(page, out var btn)) btn.Style = (Style)FindResource("NavButtonActiveStyle");

        _currentPage = page;
        (PageTitle.Text, PageSubtitle.Text) = page switch
        {
            "Dashboard" => ("Dashboard",    "Server overview and live statistics"),
            "Logs"      => ("Live Logs",    "Real-time RADIUS server log stream"),
            "Users"     => ("Users",        "Manage local user accounts"),
            "NAS"       => ("NAS / Clients","Configure network access servers and shared secrets"),
            "Settings"  => ("Settings",     "Server configuration"),
            _           => (page, ""),
        };
    }

    // ── Grid bindings ─────────────────────────────────────────────────────────
    private void SetupGridBindings()
    {
        DashboardEventGrid.ItemsSource = _authEvents;
        UsersGrid.ItemsSource          = _users;
        NasGrid.ItemsSource            = _nasClients;
    }

    // ── Stats timer ───────────────────────────────────────────────────────────
    private void SetupStatsTimer()
    {
        _statsTimer.Interval = TimeSpan.FromSeconds(1);
        _statsTimer.Tick    += (_, _) =>
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
        };
    }

    // ── Server start / stop ───────────────────────────────────────────────────
    private void BtnToggleServer_Click(object sender, RoutedEventArgs e)
    {
        if (_server == null || !_server.IsRunning) StartServer();
        else StopServer();
    }

    private void StartServer()
    {
        try
        {
            var config = new RadiusServerConfig
            {
                AuthPort    = int.TryParse(TxtAuthPort.Text, out int ap) ? ap : 1812,
                AcctPort    = int.TryParse(TxtAcctPort.Text, out int cp) ? cp : 1813,
                BindAddress = string.IsNullOrWhiteSpace(TxtBindAddr.Text) ? "0.0.0.0" : TxtBindAddr.Text.Trim(),
                DataDir     = string.IsNullOrWhiteSpace(TxtDataDir.Text)  ? "data"    : TxtDataDir.Text.Trim(),
            };

            // Route Core log lines into the GUI log — GuiRadiusLogger only,
            // so we get ONE log line per event (fixes the duplicate-line bug)
            var guiLogger = new GuiRadiusLogger(msg =>
                Dispatcher.InvokeAsync(() => AppendLog(msg)));

            _server = new RadiusServer(config, guiLogger);

            // Auth events → dashboard grid
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
                    while (_authEvents.Count > 200)
                        _authEvents.RemoveAt(_authEvents.Count - 1);
                });

            _server.Start();
            RefreshUsersGrid();
            RefreshNasGrid();
            _statsTimer.Start();
            UpdateServerStatus(running: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to start server:\n\n{ex.Message}",
                "Simple Radius — Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StopServer()
    {
        _statsTimer.Stop();
        _server?.Stop();
        UpdateServerStatus(running: false);
        AppendLog($"[{DateTime.Now:HH:mm:ss.fff}] [INF] Server stopped.");
    }

    private void UpdateServerStatus(bool running)
    {
        if (running)
        {
            StatusDotColor.Color    = (System.Windows.Media.Color)FindResource("AccentGreen");
            StatusTextColor.Color   = (System.Windows.Media.Color)FindResource("AccentGreen");
            StatusChipBg.Color      = System.Windows.Media.Color.FromRgb(0x0D, 0x26, 0x1E);
            StatusText.Text         = "Server Running";
            BtnToggleServer.Style   = (Style)FindResource("BtnDanger");
            BtnToggleServer.Content = "Stop Server";
            StatUptime.Text         = "0m 00s";
        }
        else
        {
            StatusDotColor.Color    = (System.Windows.Media.Color)FindResource("AccentRed");
            StatusTextColor.Color   = (System.Windows.Media.Color)FindResource("AccentRed");
            StatusChipBg.Color      = System.Windows.Media.Color.FromRgb(0x26, 0x0D, 0x0D);
            StatusText.Text         = "Server Stopped";
            BtnToggleServer.Style   = (Style)FindResource("BtnPrimary");
            BtnToggleServer.Content = "Start Server";
            StatUptime.Text         = "—";
            UptimeText.Text         = "Uptime: —";
        }
    }

    // ── Live log ──────────────────────────────────────────────────────────────
    private void AppendLog(string message)
    {
        LogListBox.Items.Add(message);
        while (LogListBox.Items.Count > 2000) LogListBox.Items.RemoveAt(0);
        if (ChkAutoScroll.IsChecked == true && LogListBox.Items.Count > 0)
            LogListBox.ScrollIntoView(LogListBox.Items[^1]);
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        => LogListBox.Items.Clear();

    // ══════════════════════════════════════════════════════════════════════════
    // USERS
    // ══════════════════════════════════════════════════════════════════════════

    private void RefreshUsersGrid()
    {
        if (_server == null) return;
        _users.Clear();
        foreach (var u in _server.Users.GetAll()) _users.Add(u);
    }

    // ── Save button — handles both Add and Edit ───────────────────────────────
    private void BtnSaveUser_Click(object sender, RoutedEventArgs e)
    {
        HideUserError();
        if (_server == null) { ShowUserError("Start the server first."); return; }

        string username = TxtNewUsername.Text.Trim();
        string password = PwdNewPassword.Password;
        string group    = (CmbUserGroup.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "default";
        string desc     = TxtNewUserDesc.Text.Trim();

        if (string.IsNullOrEmpty(username)) { ShowUserError("Username is required."); return; }
        if (!int.TryParse(TxtNewVlan.Text.Trim(), out int vlanId) || vlanId < 0 || vlanId > 4094)
        { ShowUserError("VLAN ID must be 0–4094."); return; }

        if (_editingUsername != null)
        {
            // ── EDIT MODE ────────────────────────────────────────────────────
            // Fetch existing entry to preserve password if field left blank
            var existing = _server.Users.Find(_editingUsername);
            if (existing == null) { ShowUserError("User no longer exists."); return; }

            var updated = new UserEntry
            {
                Username              = _editingUsername,   // username is not editable
                Password              = string.IsNullOrEmpty(password) ? existing.Password : password,
                Group                 = group,
                VlanId                = vlanId,
                IsEnabled             = existing.IsEnabled,
                Description           = string.IsNullOrEmpty(desc) ? null : desc,
                SessionTimeoutSeconds = existing.SessionTimeoutSeconds,
            };

            _server.Users.Update(updated);
            AppendLog($"[{DateTime.Now:HH:mm:ss.fff}] [INF] User updated: {_editingUsername}");
            ClearUserForm();
        }
        else
        {
            // ── ADD MODE ─────────────────────────────────────────────────────
            if (string.IsNullOrEmpty(password)) { ShowUserError("Password is required."); return; }

            var user = new UserEntry
            {
                Username    = username,
                Password    = password,
                Group       = group,
                VlanId      = vlanId,
                IsEnabled   = true,
                Description = string.IsNullOrEmpty(desc) ? null : desc,
            };

            if (!_server.Users.Add(user))
            { ShowUserError($"User '{username}' already exists."); return; }

            AppendLog($"[{DateTime.Now:HH:mm:ss.fff}] [INF] User added: {username} (Group={group}, VLAN={vlanId})");
            ClearUserForm();
        }

        RefreshUsersGrid();
    }

    // ── Edit button — populate form from selected row ─────────────────────────
    private void BtnEditUser_Click(object sender, RoutedEventArgs e)
    {
        if (_server == null || UsersGrid.SelectedItem is not UserEntry user) return;

        _editingUsername = user.Username;

        // Populate form fields
        TxtNewUsername.Text      = user.Username;
        TxtNewUsername.IsEnabled = false;           // username cannot change
        PwdNewPassword.Password  = "";              // blank = keep existing password
        TxtNewVlan.Text          = user.VlanId.ToString();
        TxtNewUserDesc.Text      = user.Description ?? "";

        // Set group combobox
        foreach (ComboBoxItem item in CmbUserGroup.Items)
            if (item.Content?.ToString() == user.Group) { CmbUserGroup.SelectedItem = item; break; }

        // Show edit banner + update button label
        UserEditBanner.Visibility     = Visibility.Visible;
        UserEditBannerName.Text       = user.Username;
        UserFormTitle.Text            = "Edit User";
        BtnSaveUser.Content           = "Update User";
        BtnCancelUserEdit.Visibility  = Visibility.Visible;
        HideUserError();

        // Scroll form into view
        PageUsers.ScrollToTop();
    }

    private void BtnCancelUserEdit_Click(object sender, RoutedEventArgs e)
        => ClearUserForm();

    private void ClearUserForm()
    {
        _editingUsername             = null;
        TxtNewUsername.Text          = "";
        TxtNewUsername.IsEnabled     = true;
        PwdNewPassword.Password      = "";
        TxtNewVlan.Text              = "0";
        TxtNewUserDesc.Text          = "";
        CmbUserGroup.SelectedIndex   = 0;
        UserFormTitle.Text           = "Add New User";
        BtnSaveUser.Content          = "Add User";
        UserEditBanner.Visibility    = Visibility.Collapsed;
        BtnCancelUserEdit.Visibility = Visibility.Collapsed;
        HideUserError();
    }

    private void BtnDeleteUser_Click(object sender, RoutedEventArgs e)
    {
        if (_server == null || UsersGrid.SelectedItem is not UserEntry user) return;
        if (MessageBox.Show($"Delete user '{user.Username}'?", "Confirm Delete",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        _server.Users.Remove(user.Username);
        if (_editingUsername == user.Username) ClearUserForm();
        RefreshUsersGrid();
        AppendLog($"[{DateTime.Now:HH:mm:ss.fff}] [INF] User deleted: {user.Username}");
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
        AppendLog($"[{DateTime.Now:HH:mm:ss.fff}] [INF] User '{user.Username}' → Enabled={updated.IsEnabled}");
    }

    private void UsersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void ShowUserError(string msg)
        { UserFormError.Text = msg; UserFormError.Visibility = Visibility.Visible; }
    private void HideUserError()
        => UserFormError.Visibility = Visibility.Collapsed;

    // ══════════════════════════════════════════════════════════════════════════
    // NAS / CLIENTS
    // ══════════════════════════════════════════════════════════════════════════

    private void RefreshNasGrid()
    {
        if (_server == null) return;
        _nasClients.Clear();
        foreach (var n in _server.Nas.GetAll()) _nasClients.Add(n);
    }

    // ── Save button — handles both Add and Edit ───────────────────────────────
    private void BtnSaveNas_Click(object sender, RoutedEventArgs e)
    {
        HideNasError();
        if (_server == null) { ShowNasError("Start the server first."); return; }

        string name   = TxtNasName.Text.Trim();
        string ip     = TxtNasIp.Text.Trim();
        string secret = PwdNasSecret.Password;
        string vendor = (CmbNasVendor.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Generic";
        string desc   = TxtNasDesc.Text.Trim();

        if (string.IsNullOrEmpty(name)) { ShowNasError("Name is required."); return; }
        if (string.IsNullOrEmpty(ip))   { ShowNasError("IP Address is required."); return; }

        if (_editingNasName != null)
        {
            // ── EDIT MODE ────────────────────────────────────────────────────
            var existing = _server.Nas.GetAll().FirstOrDefault(n => n.Name == _editingNasName);
            if (existing == null) { ShowNasError("NAS entry no longer exists."); return; }

            var updated = new NasClient
            {
                Name         = _editingNasName,
                IpAddress    = ip,
                SharedSecret = string.IsNullOrEmpty(secret) ? existing.SharedSecret : secret,
                Vendor       = vendor,
                Description  = string.IsNullOrEmpty(desc) ? null : desc,
            };

            _server.Nas.Update(updated);
            AppendLog($"[{DateTime.Now:HH:mm:ss.fff}] [INF] NAS updated: {_editingNasName} ({ip})");
            ClearNasForm();
        }
        else
        {
            // ── ADD MODE ─────────────────────────────────────────────────────
            if (string.IsNullOrEmpty(secret)) { ShowNasError("Shared secret is required."); return; }
            if (secret.Length < 6)            { ShowNasError("Shared secret must be at least 6 characters."); return; }

            var nas = new NasClient
            {
                Name         = name,
                IpAddress    = ip,
                SharedSecret = secret,
                Vendor       = vendor,
                Description  = string.IsNullOrEmpty(desc) ? null : desc,
            };

            if (!_server.Nas.Add(nas))
            { ShowNasError($"A NAS named '{name}' already exists."); return; }

            AppendLog($"[{DateTime.Now:HH:mm:ss.fff}] [INF] NAS added: {name} ({ip}) [{vendor}]");
            ClearNasForm();
        }

        RefreshNasGrid();
    }

    // ── Edit button — populate form from selected row ─────────────────────────
    private void BtnEditNas_Click(object sender, RoutedEventArgs e)
    {
        if (_server == null || NasGrid.SelectedItem is not NasClient nas) return;

        _editingNasName = nas.Name;

        TxtNasName.Text      = nas.Name;
        TxtNasName.IsEnabled = false;           // name is the key — cannot change
        TxtNasIp.Text        = nas.IpAddress;
        PwdNasSecret.Password = "";             // blank = keep existing secret
        TxtNasDesc.Text      = nas.Description ?? "";

        foreach (ComboBoxItem item in CmbNasVendor.Items)
            if (item.Content?.ToString() == nas.Vendor) { CmbNasVendor.SelectedItem = item; break; }

        NasEditBanner.Visibility    = Visibility.Visible;
        NasEditBannerName.Text      = nas.Name;
        NasFormTitle.Text           = "Edit NAS Client";
        BtnSaveNas.Content          = "Update NAS";
        BtnCancelNasEdit.Visibility = Visibility.Visible;
        HideNasError();

        PageNas.ScrollToTop();
    }

    private void BtnCancelNasEdit_Click(object sender, RoutedEventArgs e)
        => ClearNasForm();

    private void ClearNasForm()
    {
        _editingNasName             = null;
        TxtNasName.Text             = "";
        TxtNasName.IsEnabled        = true;
        TxtNasIp.Text               = "";
        PwdNasSecret.Password       = "";
        TxtNasDesc.Text             = "";
        CmbNasVendor.SelectedIndex  = 0;
        NasFormTitle.Text           = "Add NAS Client";
        BtnSaveNas.Content          = "Add NAS";
        NasEditBanner.Visibility    = Visibility.Collapsed;
        BtnCancelNasEdit.Visibility = Visibility.Collapsed;
        HideNasError();
    }

    private void BtnDeleteNas_Click(object sender, RoutedEventArgs e)
    {
        if (_server == null || NasGrid.SelectedItem is not NasClient nas) return;
        if (MessageBox.Show($"Delete NAS '{nas.Name}'?", "Confirm Delete",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        _server.Nas.Remove(nas.Name);
        if (_editingNasName == nas.Name) ClearNasForm();
        RefreshNasGrid();
        AppendLog($"[{DateTime.Now:HH:mm:ss.fff}] [INF] NAS deleted: {nas.Name}");
    }

    private void NasGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void ShowNasError(string msg)
        { NasFormError.Text = msg; NasFormError.Visibility = Visibility.Visible; }
    private void HideNasError()
        => NasFormError.Visibility = Visibility.Collapsed;

    // ── Window close ──────────────────────────────────────────────────────────
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_server?.IsRunning == true)
        {
            var result = MessageBox.Show("The RADIUS server is still running.\nStop it and exit?",
                "Simple Radius", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.No) { e.Cancel = true; return; }
        }
        _statsTimer.Stop();
        _server?.Dispose();
        base.OnClosing(e);
    }
}

// ── GUI log bridge ────────────────────────────────────────────────────────────
internal sealed class GuiRadiusLogger : IRadiusLogger
{
    private readonly Action<string> _append;
    public GuiRadiusLogger(Action<string> append) => _append = append;
    private static string Now => DateTime.Now.ToString("HH:mm:ss.fff");
    public void Info(string message)  => _append($"[{Now}] [INF] {message}");
    public void Warn(string message)  => _append($"[{Now}] [WRN] {message}");
    public void Error(string message, Exception? ex = null)
        => _append($"[{Now}] [ERR] {message}{(ex != null ? $" — {ex.Message}" : "")}");
}
