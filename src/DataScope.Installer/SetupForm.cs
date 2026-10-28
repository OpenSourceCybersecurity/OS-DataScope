using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DataScope.Installer;

/// <summary>
/// The setup wizard: welcome, accounts, database, web address, ready, installing, finished. It only collects answers
/// and shows progress; Install-DataScope.ps1 from the package does the work, checks this server first and stops
/// before changing anything when a check fails.
/// </summary>
internal sealed class SetupForm : Form
{
    private enum Step { Welcome, Accounts, Database, Web, Ready, Installing, Finished }

    private readonly SetupSession _session;
    private readonly Label _title = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 13f, FontStyle.Bold) };
    private readonly Label _subtitle = new() { AutoSize = true, MaximumSize = new Size(600, 0) };
    private readonly Panel _body = new() { Dock = DockStyle.Fill, Padding = new Padding(24, 12, 24, 12) };
    private readonly Button _back = new() { Text = "< Back", Width = 90 };
    private readonly Button _next = new() { Text = "Next >", Width = 90 };
    private readonly Button _cancel = new() { Text = "Cancel", Width = 90 };
    private readonly Dictionary<Step, Control> _pages = [];
    private Step _step = Step.Welcome;
    private bool _prepared;
    private bool _upgrade;
    private bool _running;

    // Welcome
    private readonly Label _welcomeText = Paragraph("");
    private readonly ProgressBar _unpacking = new() { Width = 400, Height = 18 };

    // Accounts
    private readonly TextBox _scanAccount = new() { Width = 300 };
    private readonly Label _scanAccountFound = Note("");
    private readonly TextBox _scanPassword = new() { Width = 300, UseSystemPasswordChar = true };
    private readonly TextBox _adminGroup = new() { Width = 300 };
    private readonly Label _adminGroupFound = Note("");

    // Database
    private readonly RadioButton _bundled = new() { AutoSize = true, Checked = true };
    private readonly RadioButton _existing = new() { AutoSize = true, Text = "Use a PostgreSQL server I already have (version 16 or later)" };
    private readonly TextBox _dbHost = new() { Width = 300, Text = "localhost" };
    private readonly NumericUpDown _dbPort = new() { Width = 90, Minimum = 1, Maximum = 65535, Value = 5432 };
    private readonly TextBox _dbUser = new() { Width = 300, Text = "postgres" };
    private readonly TextBox _dbPassword = new() { Width = 300, UseSystemPasswordChar = true };

    // Web address
    private readonly TextBox _hostName = new() { Width = 300 };
    private readonly NumericUpDown _httpsPort = new() { Width = 90, Minimum = 1, Maximum = 65535, Value = 443 };
    private readonly ComboBox _certificate = new() { Width = 420, DropDownStyle = ComboBoxStyle.DropDownList };

    // Ready, installing, finished
    private readonly Label _summary = Paragraph("");
    private readonly Label _currentStep = new() { AutoSize = true, MaximumSize = new Size(590, 0), Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold) };
    private readonly ProgressBar _working = new() { Width = 590, Height = 18, Style = ProgressBarStyle.Marquee };
    private readonly TextBox _log = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        WordWrap = true,
        Width = 590,
        Height = 230,
        Font = new Font(FontFamily.GenericMonospace, 8.5f),
        BackColor = SystemColors.Window,
    };
    private readonly Label _finishedText = Paragraph("");
    private readonly List<string> _errorLines = [];
    private string? _address;

    public SetupForm(SetupSession session)
    {
        _session = session;
        Text = "Data Scope setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        Font = SystemFonts.MessageBoxFont;
        ClientSize = new Size(660, 500);

        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 78,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(20, 14, 20, 6),
            BackColor = SystemColors.Window,
        };
        header.Controls.AddRange([_title, _subtitle]);
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12, 10, 12, 8),
        };
        buttons.Controls.AddRange([_cancel, _next, _back]);
        Controls.Add(_body);
        Controls.Add(new Label { Dock = DockStyle.Bottom, Height = 1, BorderStyle = BorderStyle.Fixed3D });
        Controls.Add(buttons);
        Controls.Add(new Label { Dock = DockStyle.Top, Height = 1, BorderStyle = BorderStyle.Fixed3D });
        Controls.Add(header);

        _back.Click += (_, _) => GoBack();
        _next.Click += async (_, _) => await GoNextAsync();
        _cancel.Click += (_, _) => Close();
        AcceptButton = _next;
        FormClosing += (_, e) =>
        {
            if (_running)
            {
                MessageBox.Show(this, "Setup is still working. Stopping it halfway could leave Data Scope half installed, so please wait.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                e.Cancel = true;
            }
        };
        _scanAccount.Leave += (_, _) => ShowFound(_scanAccount, _scanAccountFound, "account");
        _scanAccount.TextChanged += (_, _) => _scanPassword.Enabled = !_scanAccount.Text.Trim().EndsWith("$", StringComparison.Ordinal);
        _adminGroup.Leave += (_, _) => ShowFound(_adminGroup, _adminGroupFound, "group");
        _existing.CheckedChanged += (_, _) => EnableDatabaseFields();
        Shown += async (_, _) => await PrepareAsync();

        BuildPages();
        ShowStep(Step.Welcome);
    }

    private void BuildPages()
    {
        _pages[Step.Welcome] = Stack(
            _welcomeText,
            Note("Setup checks this server first and stops before changing anything if something is missing."),
            _unpacking);

        _pages[Step.Accounts] = Grid(
            Span(Paragraph("Data Scope needs two things from your domain. A domain admin can create both with " +
                "Prepare-DataScopeDomain.ps1, which comes with Data Scope.")),
            Row("Scan account:", _scanAccount),
            Row("", _scanAccountFound),
            Row("Password:", _scanPassword),
            Row("", Note("Not needed for a gMSA (a name ending in $). The scan account only reads; it must not be an admin.")),
            Row("Admin group:", _adminGroup),
            Row("", _adminGroupFound),
            Row("", Note("Members of this group run Data Scope. Other roles can be added later.")),
            Link("Don't have these yet?", ShowDomainHelp));

        _pages[Step.Database] = Grid(
            Span(Paragraph("Data Scope keeps its results in a PostgreSQL database.")),
            Span(_bundled),
            Span(Note("Installed as the service \"Data Scope Database\", reachable from this server only. Its password is " +
                "generated and kept encrypted here. Nothing else to set up.")),
            Span(_existing),
            Row("Server:", _dbHost),
            Row("Port:", _dbPort),
            Row("Admin login:", _dbUser),
            Row("Password:", _dbPassword),
            Row("", Note("Used once to create the Data Scope database and its two logins. It is not saved.")));

        _pages[Step.Web] = Grid(
            Span(Paragraph("People open Data Scope in their browser and sign in with their Windows account.")),
            Row("Server name:", _hostName),
            Row("Port:", _httpsPort),
            Row("Certificate:", _certificate),
            Row("", Note("A certificate from your own certificate authority avoids browser warnings. A self-signed one works " +
                "too: setup saves it so you can trust it on each computer.")));

        _pages[Step.Ready] = Stack(_summary);
        _pages[Step.Installing] = Stack(_currentStep, _working, _log);
        _pages[Step.Finished] = Stack(_finishedText,
            Buttons(("Open Data Scope", OpenDataScope), ("Show the backup key", ShowBackupKey), ("Open the install logs", OpenLogs)));
    }

    private async Task PrepareAsync()
    {
        _upgrade = SetupSession.IsInstalled();
        _welcomeText.Text = "Unpacking Data Scope...";
        try
        {
            await Task.Run(() => _session.Prepare(p => BeginInvoke(new Action(() => _unpacking.Value = (int)(p * 100)))));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            _welcomeText.Text = "Setup can't continue: " + e.Message;
            _next.Enabled = false;
            return;
        }
        _prepared = true;
        _unpacking.Visible = false;
        var version = _session.Version;
        _title.Text = $"Welcome to Data Scope {version}";
        if (_upgrade)
        {
            var installed = SetupSession.InstalledVersion();
            _welcomeText.Text = $"Data Scope {installed ?? ""} is installed on this server. Setup upgrades it to {version}.".Replace("  ", " ") +
                Environment.NewLine + Environment.NewLine +
                "Settings, scan results and backups are kept. The database is backed up before it is upgraded.";
            _next.Text = "Upgrade";
        }
        else
        {
            _welcomeText.Text = "Data Scope finds sensitive files on your Windows file shares and shows who can open them. " +
                "It only reads: it never changes files, permissions or Active Directory." + Environment.NewLine + Environment.NewLine +
                "Setup asks a few questions, then installs Data Scope on this server. It takes a few minutes.";
            FillDefaults();
        }
        _next.Enabled = true;
    }

    private void FillDefaults()
    {
        _scanAccount.Text = SetupSession.Suggest("svc-datascope$", "svc-datascope") ?? "";
        _adminGroup.Text = SetupSession.Suggest("DataScope-Admins") ?? "";
        _hostName.Text = SetupSession.DefaultHostName();
        _certificate.Items.AddRange(SetupSession.Certificates().Cast<object>().ToArray());
        _certificate.SelectedIndex = 0;
        var bundledVersion = _session.BundledDatabaseVersion;
        if (bundledVersion is null)
        {
            _bundled.Text = "Install PostgreSQL with Data Scope (not included in this package)";
            _bundled.Enabled = false;
            _existing.Checked = true;
        }
        else
        {
            _bundled.Text = $"Install PostgreSQL {bundledVersion} with Data Scope (recommended)".Replace("  ", " ");
        }
        EnableDatabaseFields();
        if (_scanAccount.Text.Length > 0)
        {
            ShowFound(_scanAccount, _scanAccountFound, "account");
        }
        if (_adminGroup.Text.Length > 0)
        {
            ShowFound(_adminGroup, _adminGroupFound, "group");
        }
    }

    private void ShowStep(Step step)
    {
        _step = step;
        _body.Controls.Clear();
        _body.Controls.Add(_pages[step]);
        (_title.Text, _subtitle.Text) = step switch
        {
            Step.Welcome => (_title.Text.Length > 0 ? _title.Text : "Data Scope setup", ""),
            Step.Accounts => ("Accounts", "The account that reads your file shares, and the group that runs Data Scope."),
            Step.Database => ("Database", "Where Data Scope keeps its results."),
            Step.Web => ("Web address", "How people reach Data Scope."),
            Step.Ready => (_upgrade ? "Ready to upgrade" : "Ready to install", "Check your answers, then start."),
            Step.Installing => (_upgrade ? "Upgrading Data Scope" : "Installing Data Scope", "This takes a few minutes. Please keep this window open."),
            _ => ("Finished", ""),
        };
        _back.Enabled = step is Step.Accounts or Step.Database or Step.Web or Step.Ready;
        _next.Visible = step != Step.Finished;
        _next.Enabled = step != Step.Welcome || _prepared;
        _next.Text = step switch
        {
            Step.Ready => _upgrade ? "Upgrade" : "Install",
            Step.Welcome when _upgrade => "Upgrade",
            _ => "Next >",
        };
        _cancel.Text = step == Step.Finished ? "Close" : "Cancel";
        if (step == Step.Installing)
        {
            _next.Enabled = false;
            _back.Enabled = false;
        }
    }

    private void GoBack()
    {
        ShowStep(_step switch
        {
            Step.Accounts => Step.Welcome,
            Step.Database => Step.Accounts,
            Step.Web => Step.Database,
            Step.Ready => _upgrade ? Step.Welcome : Step.Web,
            Step.Installing => Step.Ready,
            _ => _step,
        });
    }

    private async Task GoNextAsync()
    {
        switch (_step)
        {
            case Step.Welcome when _upgrade:
                ShowSummary();
                ShowStep(Step.Ready);
                break;
            case Step.Welcome:
                ShowStep(Step.Accounts);
                break;
            case Step.Accounts when AccountsOk():
                ShowStep(Step.Database);
                break;
            case Step.Database when DatabaseOk():
                ShowStep(Step.Web);
                break;
            case Step.Web when Complain(Options().Problems()):
                ShowSummary();
                ShowStep(Step.Ready);
                break;
            case Step.Ready:
                await InstallAsync();
                break;
        }
    }

    private SetupOptions Options() => new()
    {
        ScanAccount = _scanAccount.Text.Trim(),
        ScanPassword = _scanPassword.Text,
        AdminGroup = _adminGroup.Text.Trim(),
        Database = _existing.Checked ? DatabaseChoice.Existing : DatabaseChoice.Bundled,
        DatabaseHost = _dbHost.Text.Trim(),
        DatabasePort = (int)_dbPort.Value,
        DatabaseAdminUser = _dbUser.Text.Trim(),
        DatabaseAdminPassword = _dbPassword.Text,
        HostName = _hostName.Text.Trim(),
        HttpsPort = (int)_httpsPort.Value,
        CertificateThumbprint = (_certificate.SelectedItem as CertificateChoice)?.Thumbprint,
    };

    private bool AccountsOk()
    {
        var problems = Options().Problems().Where(p => p.Contains("scan account") || p.Contains("admin group")).ToList();
        if (problems.Count == 0)
        {
            if (SetupSession.FindAccount(_scanAccount.Text) is null)
            {
                problems.Add($"No account called '{_scanAccount.Text.Trim()}' was found. Check the spelling, and use DOMAIN\\Name.");
            }
            if (SetupSession.FindAccount(_adminGroup.Text) is null)
            {
                problems.Add($"No group called '{_adminGroup.Text.Trim()}' was found. Check the spelling, and use DOMAIN\\Name.");
            }
        }
        return Complain(problems);
    }

    private bool DatabaseOk() => Complain(Options().Problems().Where(p => p.Contains("PostgreSQL")).ToList());

    private bool Complain(IReadOnlyList<string> problems)
    {
        if (problems.Count == 0)
        {
            return true;
        }
        MessageBox.Show(this, string.Join(Environment.NewLine + Environment.NewLine, problems), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    private void ShowSummary()
    {
        if (_upgrade)
        {
            _summary.Text = $"Setup will upgrade Data Scope to {_session.Version}." + Environment.NewLine + Environment.NewLine +
                "It stops the Data Scope services for a few minutes, backs up the database, replaces the programs, updates the " +
                "database and starts the services again. Settings and scan results are kept.";
            return;
        }
        var o = Options();
        var text = new StringBuilder();
        text.AppendLine($"Data Scope {_session.Version} will be installed in C:\\Program Files\\Data Scope.").AppendLine();
        text.AppendLine($"Scan account:  {o.ScanAccount}");
        text.AppendLine($"Admin group:  {o.AdminGroup}");
        text.AppendLine(o.Database == DatabaseChoice.Bundled
            ? $"Database:  PostgreSQL {_session.BundledDatabaseVersion}, installed with Data Scope"
            : $"Database:  {o.DatabaseHost}, port {o.DatabasePort} (admin login {o.DatabaseAdminUser})");
        text.AppendLine($"Web address:  https://{o.HostName}{(o.HttpsPort == 443 ? "" : ":" + SetupOptions.Port(o.HttpsPort))}/");
        text.AppendLine($"Certificate:  {(o.CertificateThumbprint is null ? "a new self-signed certificate" : o.CertificateThumbprint)}").AppendLine();
        text.AppendLine("Setup first checks this server. If a check fails it stops with nothing changed, and says what to fix.");
        _summary.Text = text.ToString();
    }

    private async Task InstallAsync()
    {
        _log.Clear();
        _errorLines.Clear();
        _currentStep.ForeColor = SystemColors.ControlText;
        _currentStep.Text = "Starting...";
        _working.Style = ProgressBarStyle.Marquee;
        ShowStep(Step.Installing);
        var command = InstallCommand.Build(SetupSession.PowerShellPath, _session.PackageFolder!, Options(), _upgrade, _session.IsSigned());
        _running = true;
        _cancel.Enabled = false;
        int code;
        try
        {
            code = await Task.Run(() => SetupSession.Run(command, (line, isError) => BeginInvoke(new Action(() => ShowLine(line, isError)))));
        }
        finally
        {
            _running = false;
            _cancel.Enabled = true;
        }
        _working.Style = ProgressBarStyle.Continuous;
        if (code == 0)
        {
            _working.Value = 100;
            ShowFinished();
            return;
        }
        _working.Value = 0;
        _currentStep.ForeColor = Color.Firebrick;
        _currentStep.Text = "Setup stopped: " + (InstallOutput.Reason(_errorLines) ?? $"the installer ended with exit code {code}.") +
            Environment.NewLine + "Fix what it says, then press Back and try again. The details are below and in " + SetupSession.InstallLogFolder + ".";
        _back.Enabled = true;
        _cancel.Text = "Close";
    }

    private void ShowLine(string line, bool isError)
    {
        _log.AppendText(line + Environment.NewLine);
        if (isError)
        {
            _errorLines.Add(line);
            return;
        }
        var (kind, text) = InstallOutput.Classify(line);
        if (kind == OutputKind.Step)
        {
            _currentStep.Text = text;
        }
    }

    private void ShowFinished()
    {
        using (var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\DataScope"))
        {
            _address = key?.GetValue("URLInfoAbout") as string;
        }
        var text = new StringBuilder();
        text.AppendLine($"Data Scope {_session.Version} is running{(_address is null ? "." : " at " + _address)}").AppendLine();
        if (!_upgrade)
        {
            var o = Options();
            text.AppendLine("Next steps:");
            if (o.CertificateThumbprint is null)
            {
                text.AppendLine(@"- Trust the self-signed certificate on each computer that opens Data Scope: copy " +
                    @"C:\ProgramData\DataScope\datascope-https.cer there, double-click it, Install Certificate, Local Machine, " +
                    "Trusted Root Certification Authorities (or push it with Group Policy).");
            }
            text.AppendLine("- Add the address to Internet Options, Security, Local intranet, Sites, so browsers sign you in with Windows.");
            text.AppendLine($"- Open Data Scope as a member of {o.AdminGroup}, then Servers & scans, Add server.");
            text.AppendLine("- Save the backup key in your password manager now: backups can only be restored with it.");
        }
        text.AppendLine().AppendLine("Data Scope is listed in Settings, Apps, where you can uninstall it.");
        _finishedText.Text = text.ToString();
        ShowStep(Step.Finished);
        _title.Text = _upgrade ? "Data Scope is upgraded" : "Data Scope is installed";
    }

    private void OpenDataScope()
    {
        if (_address is not null)
        {
            Process.Start(new ProcessStartInfo(_address) { UseShellExecute = true });
        }
    }

    private void OpenLogs() => Process.Start(new ProcessStartInfo(SetupSession.InstallLogFolder) { UseShellExecute = true });

    private void ShowBackupKey()
    {
        string key;
        try
        {
            key = SetupSession.ReadBackupKey();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, "The backup key could not be read: " + e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        using var dialog = new Form
        {
            Text = "Backup key",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(520, 170),
            Font = Font,
        };
        var box = new TextBox { Text = key, ReadOnly = true, Width = 480, Font = new Font(FontFamily.GenericMonospace, 9f) };
        var copy = new Button { Text = "Copy", Width = 90 };
        copy.Click += (_, _) => Clipboard.SetText(key);
        dialog.Controls.Add(Stack(
            Paragraph("Keep this key in your password manager, away from this server. Backups can only be restored with it."),
            box, copy));
        dialog.ShowDialog(this);
    }

    private void ShowDomainHelp()
    {
        var script = Path.Combine(_session.PackageFolder ?? "", "Prepare-DataScopeDomain.ps1");
        MessageBox.Show(this,
            "On a domain controller, a domain admin runs the domain preparation script that comes with Data Scope, in PowerShell as administrator:" +
            Environment.NewLine + Environment.NewLine +
            $".\\Prepare-DataScopeDomain.ps1 -DataScopeServer {Environment.MachineName}" + Environment.NewLine + Environment.NewLine +
            "It creates the scan account svc-datascope$ (a gMSA, with no password to manage) and the DataScope-Admins group, " +
            "and asks before changing anything. Copy it from:" + Environment.NewLine + script + Environment.NewLine + Environment.NewLine +
            "Then add yourself to DataScope-Admins, and come back here.",
            "Preparing the domain", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void ShowFound(TextBox box, Label label, string what)
    {
        if (box.Text.Trim().Length == 0)
        {
            label.Text = "";
            return;
        }
        var found = SetupSession.FindAccount(box.Text);
        label.ForeColor = found is null ? Color.Firebrick : Color.DarkGreen;
        label.Text = found is null ? $"No {what} with this name was found." : $"Found: {found}";
        if (found is not null)
        {
            box.Text = found;
        }
    }

    private void EnableDatabaseFields()
    {
        foreach (var field in new Control[] { _dbHost, _dbPort, _dbUser, _dbPassword })
        {
            field.Enabled = _existing.Checked;
        }
    }

    private static Label Paragraph(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(590, 0), Margin = new Padding(0, 0, 0, 10) };

    private static Label Note(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = SystemColors.GrayText };

    private static FlowLayoutPanel Stack(params Control[] controls)
    {
        var stack = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        stack.Controls.AddRange(controls);
        return stack;
    }

    private static FlowLayoutPanel Buttons(params (string Text, Action OnClick)[] buttons)
    {
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 6, 0, 0) };
        foreach (var (text, onClick) in buttons)
        {
            var button = new Button { Text = text, AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
            button.Click += (_, _) => onClick();
            row.Controls.Add(button);
        }
        return row;
    }

    /// <summary>A two-column form: labels on the left, fields on the right; spanning rows for text.</summary>
    private static TableLayoutPanel Grid(params Control[][] rows)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var row in rows)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (row.Length == 1)
            {
                grid.Controls.Add(row[0]);
                grid.SetColumnSpan(row[0], 2);
            }
            else
            {
                grid.Controls.Add(row[0]);
                grid.Controls.Add(row[1]);
            }
        }
        return grid;
    }

    private static Control[] Row(string label, Control field) =>
        [new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 3) }, field];

    private static Control[] Span(Control control) => [control];

    private static Control[] Link(string text, Action onClick)
    {
        var link = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        link.LinkClicked += (_, _) => onClick();
        return [link];
    }
}
