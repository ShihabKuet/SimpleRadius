using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SimpleRadius.Core;
using SimpleRadius.Core.Accounting;
using SimpleRadius.Core.Models;
using SimpleRadius.Core.Policy;
using SimpleRadius.Core.Server;

namespace SimpleRadius.GUI;

// ── Auth event row ────────────────────────────────────────────────────────────
public sealed class AuthEventRow
{
    public DateTime Timestamp  { get; init; }
    public string   Username   { get; init; } = "";
    public string   NasIp      { get; init; } = "";
    public string   Method     { get; init; } = "";
    public bool     Accepted   { get; init; }
    public string   ResultText => Accepted ? "Accept" : "Reject";
}

public partial class MainWindow : Window
{
    // ── Server ────────────────────────────────────────────────────────────────
    private RadiusServer?   _server;
    private DispatcherTimer _statsTimer  = new();
    private DispatcherTimer _acctTimer   = new();

    // ── Grid collections ──────────────────────────────────────────────────────
    private readonly ObservableCollection<AuthEventRow>     _authEvents  = new();
    private readonly ObservableCollection<UserEntry>        _users       = new();
    private readonly ObservableCollection<NasClient>        _nasClients  = new();
    private readonly ObservableCollection<AccountingSession> _sessions   = new();
    private readonly ObservableCollection<PolicyRule>       _policies    = new();

    // ── Edit-mode state ───────────────────────────────────────────────────────
    private string? _editingUsername;
    private string? _editingNasName;
    private string? _editingPolicyName;

    // ── Nav registry ─────────────────────────────────────────────────────────
    private Dictionary<string, Button>    _navButtons = new();
    private Dictionary<string, UIElement> _pages      = new();

    // ─────────────────────────────────────────────────────────────────────────
    public MainWindow()
    {
        InitializeComponent();
        SetupNavigation();
        SetupGridBindings();
        SetupTimers();
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
            ["Accounting"]= NavAccounting,
            ["Users"]     = NavUsers,
            ["NAS"]       = NavNas,
            ["Policy"]    = NavPolicy,
            ["Settings"]  = NavSettings,
        };
        _pages = new()
        {
            ["Dashboard"] = PageDashboard,
            ["Logs"]      = PageLogs,
            ["Accounting"]= PageAccounting,
            ["Users"]     = PageUsers,
            ["NAS"]       = PageNas,
            ["Policy"]    = PagePolicy,
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

        if (_pages.TryGetValue(page, out var target))    target.Visibility = Visibility.Visible;
        if (_navButtons.TryGetValue(page, out var btn))  btn.Style = (Style)FindResource("NavButtonActiveStyle");

        (PageTitle.Text, PageSubtitle.Text) = page switch
        {
            "Dashboard"  => ("Dashboard",    "Server overview and live statistics"),
            "Logs"       => ("Live Logs",    "Real-time RADIUS server log stream"),
            "Accounting" => ("Accounting",   "Session accounting log and statistics"),
            "Users"      => ("Users",        "Manage local user accounts"),
            "NAS"        => ("NAS / Clients","Configure network access servers and shared secrets"),
            "Policy"     => ("Policies",     "Group and NAS-based access rules — first match wins"),
            "Settings"   => ("Settings",     "Server configuration"),
            _            => (page, ""),
        };

        // Refresh accounting stats when switching to that page
        if (page == "Accounting") RefreshAccountingStats();
    }

    // ── Grid bindings ─────────────────────────────────────────────────────────
    private void SetupGridBindings()
    {
        DashboardEventGrid.ItemsSource = _authEvents;
        UsersGrid.ItemsSource          = _users;
        NasGrid.ItemsSource            = _nasClients;
        AccountingGrid.ItemsSource     = _sessions;
        PolicyGrid.ItemsSource         = _policies;
    }

    // ── Timers ────────────────────────────────────────────────────────────────
    private void SetupTimers()
    {
        // Stats timer — updates dashboard counters every second
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

        // Accounting timer — refreshes active sessions every 30s
        _acctTimer.Interval = TimeSpan.FromSeconds(30);
        _acctTimer.Tick    += (_, _) => RefreshAccountingStats();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // SERVER CONTROL
    // ══════════════════════════════════════════════════════════════════════════

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

            var guiLogger = new GuiRadiusLogger(msg =>
                Dispatcher.InvokeAsync(() => AppendLog(msg)));

            _server = new RadiusServer(config, guiLogger);

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
            RefreshPolicyGrid();
            RefreshAccountingStats();

            _statsTimer.Start();
            _acctTimer.Start();
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
        _acctTimer.Stop();
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

    // ══════════════════════════════════════════════════════════════════════════
    // LIVE LOGS
    // ══════════════════════════════════════════════════════════════════════════

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
    // ACCOUNTING
    // ══════════════════════════════════════════════════════════════════════════

    private void RefreshAccountingStats()
    {
        if (_server == null) return;
        try
        {
            var (total, active, inMb, outMb) = _server.Accounting.GetStats();
            AcctStatTotal.Text  = total.ToString("N0");
            AcctStatActive.Text = active.ToString("N0");
            AcctStatIn.Text     = inMb.ToString("N0");
            AcctStatOut.Text    = outMb.ToString("N0");
        }
        catch { /* DB may not be ready yet */ }
    }

    private void BtnAcctSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_server == null) return;
        var sessions = _server.Accounting.GetSessions(
            from:     AcctFrom.SelectedDate,
            to:       AcctTo.SelectedDate?.AddDays(1),
            username: string.IsNullOrWhiteSpace(AcctUserFilter.Text) ? null : AcctUserFilter.Text.Trim());
        _sessions.Clear();
        foreach (var s in sessions) _sessions.Add(s);
        RefreshAccountingStats();
    }

    private void BtnAcctActive_Click(object sender, RoutedEventArgs e)
    {
        if (_server == null) return;
        var sessions = _server.Accounting.GetActiveSessions();
        _sessions.Clear();
        foreach (var s in sessions) _sessions.Add(s);
    }

    private void BtnAcctExport_Click(object sender, RoutedEventArgs e)
    {
        if (_server == null) return;
        try
        {
            var csv      = _server.Accounting.ExportCsv(
                AcctFrom.SelectedDate,
                AcctTo.SelectedDate?.AddDays(1),
                string.IsNullOrWhiteSpace(AcctUserFilter.Text) ? null : AcctUserFilter.Text.Trim());

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName   = $"SimpleRadius-Accounting-{DateTime.Now:yyyyMMdd-HHmm}.csv",
                DefaultExt = ".csv",
                Filter     = "CSV files (*.csv)|*.csv",
            };
            if (dlg.ShowDialog() == true)
            {
                File.WriteAllText(dlg.FileName, csv);
                MessageBox.Show($"Exported to:\n{dlg.FileName}",
                    "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Export failed:\n{ex.Message}",
                "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // USERS
    // ══════════════════════════════════════════════════════════════════════════

    private void RefreshUsersGrid()
    {
        if (_server == null) return;
        _users.Clear();
        foreach (var u in _server.Users.GetAll()) _users.Add(u);
    }

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
            var existing = _server.Users.Find(_editingUsername);
            if (existing == null) { ShowUserError("User no longer exists."); return; }

            var updated = new UserEntry
            {
                Username              = _editingUsername,
                Password              = string.IsNullOrEmpty(password) ? "" : password,
                PasswordHash          = existing.PasswordHash,
                NtHash                = existing.NtHash,
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
            if (!_server.Users.Add(user)) { ShowUserError($"User '{username}' already exists."); return; }
            AppendLog($"[{DateTime.Now:HH:mm:ss.fff}] [INF] User added: {username} (Group={group}, VLAN={vlanId})");
            ClearUserForm();
        }
        RefreshUsersGrid();
    }

    private void BtnEditUser_Click(object sender, RoutedEventArgs e)
    {
        if (_server == null || UsersGrid.SelectedItem is not UserEntry user) return;
        _editingUsername             = user.Username;
        TxtNewUsername.Text          = user.Username;
        TxtNewUsername.IsEnabled     = false;
        PwdNewPassword.Password      = "";
        TxtNewVlan.Text              = user.VlanId.ToString();
        TxtNewUserDesc.Text          = user.Description ?? "";
        foreach (ComboBoxItem item in CmbUserGroup.Items)
            if (item.Content?.ToString() == user.Group) { CmbUserGroup.SelectedItem = item; break; }
        UserEditBanner.Visibility    = Visibility.Visible;
        UserEditBannerName.Text      = user.Username;
        UserFormTitle.Text           = "Edit User";
        BtnSaveUser.Content          = "Update User";
        BtnCancelUserEdit.Visibility = Visibility.Visible;
        HideUserError();
        PageUsers.ScrollToTop();
    }

    private void BtnCancelUserEdit_Click(object sender, RoutedEventArgs e) => ClearUserForm();

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
            Password              = "",
            PasswordHash          = user.PasswordHash,
            NtHash                = user.NtHash,
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
    private void ShowUserError(string msg) { UserFormError.Text = msg; UserFormError.Visibility = Visibility.Visible; }
    private void HideUserError() => UserFormError.Visibility = Visibility.Collapsed;

    // ══════════════════════════════════════════════════════════════════════════
    // NAS
    // ══════════════════════════════════════════════════════════════════════════

    private void RefreshNasGrid()
    {
        if (_server == null) return;
        _nasClients.Clear();
        foreach (var n in _server.Nas.GetAll()) _nasClients.Add(n);
    }

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
            if (string.IsNullOrEmpty(secret)) { ShowNasError("Shared secret is required."); return; }
            if (secret.Length < 6)            { ShowNasError("Shared secret must be at least 6 characters."); return; }

            var nas = new NasClient { Name = name, IpAddress = ip, SharedSecret = secret, Vendor = vendor,
                Description = string.IsNullOrEmpty(desc) ? null : desc };
            if (!_server.Nas.Add(nas)) { ShowNasError($"A NAS named '{name}' already exists."); return; }
            AppendLog($"[{DateTime.Now:HH:mm:ss.fff}] [INF] NAS added: {name} ({ip}) [{vendor}]");
            ClearNasForm();
        }
        RefreshNasGrid();
    }

    private void BtnEditNas_Click(object sender, RoutedEventArgs e)
    {
        if (_server == null || NasGrid.SelectedItem is not NasClient nas) return;
        _editingNasName              = nas.Name;
        TxtNasName.Text              = nas.Name;
        TxtNasName.IsEnabled         = false;
        TxtNasIp.Text                = nas.IpAddress;
        PwdNasSecret.Password        = "";
        TxtNasDesc.Text              = nas.Description ?? "";
        foreach (ComboBoxItem item in CmbNasVendor.Items)
            if (item.Content?.ToString() == nas.Vendor) { CmbNasVendor.SelectedItem = item; break; }
        NasEditBanner.Visibility     = Visibility.Visible;
        NasEditBannerName.Text       = nas.Name;
        NasFormTitle.Text            = "Edit NAS Client";
        BtnSaveNas.Content           = "Update NAS";
        BtnCancelNasEdit.Visibility  = Visibility.Visible;
        HideNasError();
        PageNas.ScrollToTop();
    }

    private void BtnCancelNasEdit_Click(object sender, RoutedEventArgs e) => ClearNasForm();

    private void ClearNasForm()
    {
        _editingNasName              = null;
        TxtNasName.Text              = "";
        TxtNasName.IsEnabled         = true;
        TxtNasIp.Text                = "";
        PwdNasSecret.Password        = "";
        TxtNasDesc.Text              = "";
        CmbNasVendor.SelectedIndex   = 0;
        NasFormTitle.Text            = "Add NAS Client";
        BtnSaveNas.Content           = "Add NAS";
        NasEditBanner.Visibility     = Visibility.Collapsed;
        BtnCancelNasEdit.Visibility  = Visibility.Collapsed;
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
    private void ShowNasError(string msg) { NasFormError.Text = msg; NasFormError.Visibility = Visibility.Visible; }
    private void HideNasError() => NasFormError.Visibility = Visibility.Collapsed;

    // ══════════════════════════════════════════════════════════════════════════
    // POLICY
    // ══════════════════════════════════════════════════════════════════════════

    private void RefreshPolicyGrid()
    {
        if (_server == null) return;
        _policies.Clear();
        foreach (var p in _server.Policy.GetAll()) _policies.Add(p);
    }

    private void BtnSavePolicy_Click(object sender, RoutedEventArgs e)
    {
        HidePolicyError();
        if (_server == null) { ShowPolicyError("Start the server first."); return; }

        string name  = TxtPolicyName.Text.Trim();
        string reply = TxtPolicyReply.Text.Trim();

        if (string.IsNullOrEmpty(name)) { ShowPolicyError("Policy name is required."); return; }
        if (!int.TryParse(TxtPolicyPriority.Text.Trim(), out int priority))
        { ShowPolicyError("Priority must be a number."); return; }
        if (!int.TryParse(TxtPolicyVlan.Text.Trim(),    out int vlan))
        { ShowPolicyError("VLAN ID must be a number."); return; }
        if (!int.TryParse(TxtPolicyTimeout.Text.Trim(), out int timeout))
        { ShowPolicyError("Session timeout must be a number."); return; }
        if (!int.TryParse(TxtPolicyIdle.Text.Trim(),    out int idle))
        { ShowPolicyError("Idle timeout must be a number."); return; }

        var rule = new PolicyRule
        {
            Name               = _editingPolicyName ?? name,
            Priority           = priority,
            IsEnabled          = true,
            MatchGroup         = string.IsNullOrWhiteSpace(TxtPolicyGroup.Text)  ? null : TxtPolicyGroup.Text.Trim(),
            MatchNasIp         = string.IsNullOrWhiteSpace(TxtPolicyNasIp.Text)  ? null : TxtPolicyNasIp.Text.Trim(),
            MatchUser          = string.IsNullOrWhiteSpace(TxtPolicyUser.Text)   ? null : TxtPolicyUser.Text.Trim(),
            VlanId             = vlan,
            SessionTimeoutSecs = timeout,
            IdleTimeoutSecs    = idle,
            Reject             = ChkPolicyReject.IsChecked == true,
            ReplyMessage       = string.IsNullOrEmpty(reply) ? null : reply,
        };

        if (_editingPolicyName != null)
        {
            _server.Policy.Update(rule);
            AppendLog($"[{DateTime.Now:HH:mm:ss.fff}] [INF] Policy updated: {rule.Name}");
            ClearPolicyForm();
        }
        else
        {
            if (!_server.Policy.Add(rule))
            { ShowPolicyError($"A policy named '{name}' already exists."); return; }
            AppendLog($"[{DateTime.Now:HH:mm:ss.fff}] [INF] Policy added: {rule.Name} (Priority={priority})");
            ClearPolicyForm();
        }
        RefreshPolicyGrid();
    }

    private void BtnEditPolicy_Click(object sender, RoutedEventArgs e)
    {
        if (_server == null || PolicyGrid.SelectedItem is not PolicyRule rule) return;
        _editingPolicyName            = rule.Name;
        TxtPolicyName.Text            = rule.Name;
        TxtPolicyName.IsEnabled       = false;
        TxtPolicyPriority.Text        = rule.Priority.ToString();
        TxtPolicyGroup.Text           = rule.MatchGroup  ?? "";
        TxtPolicyNasIp.Text           = rule.MatchNasIp  ?? "";
        TxtPolicyUser.Text            = rule.MatchUser   ?? "";
        TxtPolicyVlan.Text            = rule.VlanId.ToString();
        TxtPolicyTimeout.Text         = rule.SessionTimeoutSecs.ToString();
        TxtPolicyIdle.Text            = rule.IdleTimeoutSecs.ToString();
        TxtPolicyReply.Text           = rule.ReplyMessage ?? "";
        ChkPolicyReject.IsChecked     = rule.Reject;
        PolicyEditBanner.Visibility   = Visibility.Visible;
        PolicyEditBannerName.Text     = rule.Name;
        PolicyFormTitle.Text          = "Edit Policy Rule";
        BtnSavePolicy.Content         = "Update Policy";
        BtnCancelPolicyEdit.Visibility = Visibility.Visible;
        HidePolicyError();
        PagePolicy.ScrollToTop();
    }

    private void BtnCancelPolicyEdit_Click(object sender, RoutedEventArgs e) => ClearPolicyForm();

    private void ClearPolicyForm()
    {
        _editingPolicyName             = null;
        TxtPolicyName.Text             = "";
        TxtPolicyName.IsEnabled        = true;
        TxtPolicyPriority.Text         = "50";
        TxtPolicyGroup.Text            = "";
        TxtPolicyNasIp.Text            = "";
        TxtPolicyUser.Text             = "";
        TxtPolicyVlan.Text             = "0";
        TxtPolicyTimeout.Text          = "0";
        TxtPolicyIdle.Text             = "0";
        TxtPolicyReply.Text            = "";
        ChkPolicyReject.IsChecked      = false;
        PolicyFormTitle.Text           = "Add Policy Rule";
        BtnSavePolicy.Content          = "Add Policy";
        PolicyEditBanner.Visibility    = Visibility.Collapsed;
        BtnCancelPolicyEdit.Visibility = Visibility.Collapsed;
        HidePolicyError();
    }

    private void BtnTogglePolicy_Click(object sender, RoutedEventArgs e)
    {
        if (_server == null || PolicyGrid.SelectedItem is not PolicyRule rule) return;
        var updated = new PolicyRule
        {
            Name               = rule.Name,
            Priority           = rule.Priority,
            IsEnabled          = !rule.IsEnabled,
            MatchGroup         = rule.MatchGroup,
            MatchNasIp         = rule.MatchNasIp,
            MatchUser          = rule.MatchUser,
            VlanId             = rule.VlanId,
            SessionTimeoutSecs = rule.SessionTimeoutSecs,
            IdleTimeoutSecs    = rule.IdleTimeoutSecs,
            Reject             = rule.Reject,
            ReplyMessage       = rule.ReplyMessage,
        };
        _server.Policy.Update(updated);
        RefreshPolicyGrid();
        AppendLog($"[{DateTime.Now:HH:mm:ss.fff}] [INF] Policy '{rule.Name}' → Enabled={updated.IsEnabled}");
    }

    private void BtnDeletePolicy_Click(object sender, RoutedEventArgs e)
    {
        if (_server == null || PolicyGrid.SelectedItem is not PolicyRule rule) return;
        if (MessageBox.Show($"Delete policy '{rule.Name}'?", "Confirm Delete",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _server.Policy.Remove(rule.Name);
        if (_editingPolicyName == rule.Name) ClearPolicyForm();
        RefreshPolicyGrid();
        AppendLog($"[{DateTime.Now:HH:mm:ss.fff}] [INF] Policy deleted: {rule.Name}");
    }

    private void PolicyGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
    private void ShowPolicyError(string msg) { PolicyFormError.Text = msg; PolicyFormError.Visibility = Visibility.Visible; }
    private void HidePolicyError() => PolicyFormError.Visibility = Visibility.Collapsed;

    // ══════════════════════════════════════════════════════════════════════════
    // WINDOW CLOSE
    // ══════════════════════════════════════════════════════════════════════════

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_server?.IsRunning == true)
        {
            var result = MessageBox.Show("The RADIUS server is still running.\nStop it and exit?",
                "Simple Radius", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.No) { e.Cancel = true; return; }
        }
        _statsTimer.Stop();
        _acctTimer.Stop();
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
