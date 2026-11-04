using System.Net;

namespace BiStudent;

internal sealed class LoginForm : DarkForm
{
    private readonly ClientSettings settings = ClientSettings.Load();
    private readonly TextBox server = Ui.Input("http://localhost:5080");
    private readonly TextBox login = Ui.Input("Логин");
    private readonly TextBox password = Ui.Input("Пароль");
    private readonly CheckBox remember = new() { Text = "Запомнить меня на этом компьютере", ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat, Dock = DockStyle.Fill };
    private readonly Label error = Ui.Label("", 9, Theme.Red);
    private readonly ModernButton button = new() { Text = "Войти", Primary = true, Height = 44 };
    private bool signingIn;
    public ApiClient? Api { get; private set; }

    public LoginForm(string? initialError = null)
    {
        Text = "BiStudent · Вход"; ClientSize = new Size(920, 620);
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.Background; ForeColor = Theme.Text; Font = Theme.Font(10);
        Build();
        server.Text = settings.ServerUrl;
        login.Text = settings.LastLogin;
        remember.Checked = settings.RememberMe;
        error.Text = initialError ?? "";
        password.UseSystemPasswordChar = true;
        AcceptButton = button;
        button.Click += async (_, _) => await SignInAsync();
    }

    private void Build()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Background };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
        var left = new SmoothPanel { Dock = DockStyle.Fill, Margin = new Padding(36, 28, 16, 28), Padding = new Padding(28) };
        var form = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 11, BackColor = Theme.Surface };
        foreach (var height in new[] { 48, 45, 22, 38, 22, 38, 22, 38, 36, 50, 44 }) form.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        var logo = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        var mark = new Label { Text = "✓", Width = 42, Height = 42, BackColor = Theme.Accent, ForeColor = Theme.Text, Font = Theme.Font(20, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter };
        var brand = new Label { Text = "BiStudent", Width = 240, Height = 42, ForeColor = Theme.Text, Font = Theme.Font(19, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
        logo.Controls.Add(mark); logo.Controls.Add(brand); form.Controls.Add(logo, 0, 0);
        form.Controls.Add(Ui.Label("Кабинет преподавателя", 13, Theme.Text, FontStyle.Bold), 0, 1);
        form.Controls.Add(Ui.Label("Адрес сервера", 9, Theme.Muted), 0, 2);
        server.Dock = DockStyle.Fill; form.Controls.Add(server, 0, 3);
        form.Controls.Add(Ui.Label("Логин", 9, Theme.Muted), 0, 4);
        login.Dock = DockStyle.Fill; form.Controls.Add(login, 0, 5);
        form.Controls.Add(Ui.Label("Пароль", 9, Theme.Muted), 0, 6);
        password.Dock = DockStyle.Fill; form.Controls.Add(password, 0, 7);
        form.Controls.Add(remember, 0, 8);
        button.Dock = DockStyle.Fill; button.Margin = new Padding(0, 8, 0, 0); form.Controls.Add(button, 0, 9);
        error.TextAlign = ContentAlignment.MiddleLeft; form.Controls.Add(error, 0, 10);
        left.Controls.Add(form); layout.Controls.Add(left, 0, 0);
        var right = new SmoothPanel { Dock = DockStyle.Fill, Margin = new Padding(16, 28, 36, 28), Padding = new Padding(20), BackColor = Theme.Sidebar };
        var features = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = Theme.Sidebar };
        foreach (var height in new[] { 80, 86, 86, 86, 86 }) features.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        features.Controls.Add(Ui.Label("BiStudent — учёт посещаемости и успеваемости", 14, Theme.Text, FontStyle.Bold), 0, 0);
        AddFeature(features, 1, "Отметка посещаемости по камере\nРаспознавание лиц во время занятия");
        AddFeature(features, 2, "Электронный журнал\nПосещаемость, баллы, контрольные и итог");
        AddFeature(features, 3, "Расписание на семестр\nЧётные и нечётные недели, переносы");
        AddFeature(features, 4, "Регистрация лиц студентами\nПо коду группы, с подтверждением преподавателя");
        right.Controls.Add(features); layout.Controls.Add(right, 1, 0); Controls.Add(layout);
    }

    private static void AddFeature(TableLayoutPanel container, int row, string text)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Sidebar };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.Controls.Add(Ui.Label("●", 12, Theme.Accent), 0, 0);
        panel.Controls.Add(Ui.Label(text, 10, Theme.Text), 1, 0);
        container.Controls.Add(panel, 0, row);
    }

    private async Task SignInAsync()
    {
        if (signingIn) return;
        signingIn = true; button.Enabled = false; UseWaitCursor = true; error.Text = "";
        ApiClient? client = null;
        try
        {
            var address = server.Text.Trim();
            if (LocalServer.ShouldStart(address))
            {
                error.ForeColor = Theme.Muted;
                error.Text = "Запуск сервера…";
                if (!await LocalServer.WaitUntilReadyAsync(TimeSpan.FromSeconds(60)))
                {
                    error.ForeColor = Theme.Red;
                    error.Text = "Не удалось запустить сервер BiStudent. Перезапустите программу.";
                    return;
                }
                error.ForeColor = Theme.Red;
                error.Text = "";
            }
            if (!ClientSettings.SameServer(settings.ServerUrl, address))
                settings.StoreRefreshToken(null);
            settings.ServerUrl = address; settings.LastLogin = login.Text.Trim();
            settings.RememberMe = remember.Checked;
            settings.Save();
            client = new ApiClient(settings);
            await client.LoginAsync(settings.LastLogin, password.Text, settings.RememberMe);
            await client.LoadMeAsync();
            settings.StoreRefreshToken(settings.RememberMe ? client.RefreshToken : null);
            settings.Save();
            if (settings.RememberMe)
                client.RefreshTokenChanged += token => { settings.StoreRefreshToken(token); settings.Save(); };
            Api = client; DialogResult = DialogResult.OK; Close();
        }
        catch (ApiException ex)
        {
            client?.Dispose();
            error.Text = ex.StatusCode == HttpStatusCode.Unauthorized ? "Неверный логин или пароль" : ex.Message;
        }
        catch (Exception ex)
        {
            client?.Dispose();
            error.Text = $"Не удалось войти: {ex.Message}";
        }
        finally
        {
            signingIn = false;
            error.ForeColor = Theme.Red;
            if (!IsDisposed) { button.Enabled = true; UseWaitCursor = false; }
        }
    }
}
