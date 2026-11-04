using BiStudent.Contracts;
using QRCoder;

namespace BiStudent;

internal sealed class EnrollmentCodeDialog : DarkForm
{
    private static readonly int[] Hours = [1, 24, 72, 168];
    private readonly ApiClient api;
    private readonly int groupId;
    private readonly Label codeText = Ui.Label("", 34, Theme.Text, FontStyle.Bold);
    private readonly Label expires = Ui.Label("", 10, Theme.Muted);
    private readonly Label error = Ui.Label("", 9, Theme.Red);
    private readonly PictureBox qr = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
    private readonly TextBox links = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Theme.SurfaceHover, ForeColor = Theme.Text, Font = Theme.Font(9.5f) };
    private readonly ModernComboBox duration = new() { Width = 140 };
    private readonly ModernButton issue = new() { Text = "Выдать код", Primary = true, Width = 175 };
    private readonly ModernButton revoke = new() { Text = "Отозвать код", Danger = true, Width = 165 };
    private readonly ModernButton copyCode = new() { Text = "Скопировать код", Width = 175 };
    private readonly ModernButton copyLink = new() { Text = "Скопировать ссылку", Width = 195 };
    private EnrollmentCodeDto? current;
    private bool busy;

    public EnrollmentCodeDialog(ApiClient api, int groupId, string groupName)
    {
        this.api = api;
        this.groupId = groupId;
        Text = "Код регистрации лица";
        // На низких экранах (1366×768) окно не выше рабочей области, остальное — прокруткой.
        ClientSize = new Size(780, Math.Min(800, Screen.PrimaryScreen!.WorkingArea.Height - 60));
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, RowCount = 9, BackColor = Theme.Background, Padding = new Padding(24, 10, 24, 10) };
        foreach (var height in new[] { 38, 65, 30, 210, 78, 52, 48, 126, 45 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.Controls.Add(Ui.Label(groupName, 16, Theme.Text, FontStyle.Bold), 0, 0);
        codeText.Font = new Font("Consolas", 34, FontStyle.Bold);
        layout.Controls.Add(codeText, 0, 1);
        layout.Controls.Add(expires, 0, 2);
        var qrHolder = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Padding = new Padding(0, 4, 0, 4) };
        qr.Width = 202; qr.Dock = DockStyle.Left;
        qrHolder.Controls.Add(qr);
        layout.Controls.Add(qrHolder, 0, 3);
        layout.Controls.Add(links, 0, 4);
        foreach (var text in new[] { "1 час", "1 день", "3 дня", "7 дней" }) duration.Items.Add(text);
        duration.SelectedIndex = 1;
        var controls = Ui.Row();
        controls.Controls.Add(duration); controls.Controls.Add(issue); controls.Controls.Add(revoke);
        layout.Controls.Add(controls, 0, 5);
        var copies = Ui.Row();
        copies.Controls.Add(copyCode); copies.Controls.Add(copyLink);
        layout.Controls.Add(copies, 0, 6);
        var instructions = Ui.Label("1. Студент открывает ссылку или сканирует QR-код. Для облачного адреса подходит любая сеть; для локального — сеть сервера.\n2. Вводит код и выбирает себя в списке группы.\n3. Делает три снимка лица.\n4. Вы проверяете снимки в разделе «Заявки на регистрацию» и подтверждаете.", 9.5f, Theme.Text);
        instructions.TextAlign = ContentAlignment.TopLeft;
        layout.Controls.Add(instructions, 0, 7);
        var note = Ui.Label("Код действует для всей группы. Снимки не используются, пока вы их не подтвердите.", 8.5f, Theme.Muted);
        layout.Controls.Add(note, 0, 8);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = Theme.Background, Padding = new Padding(24, 6, 24, 6) };
        var close = new ModernButton { Text = "Закрыть", Width = 130, Dock = DockStyle.Right };
        close.Click += (_, _) => Close();
        error.Dock = DockStyle.Fill;
        footer.Controls.Add(error); footer.Controls.Add(close);
        Controls.Add(layout); Controls.Add(footer);
        CancelButton = close;
        issue.Click += async (_, _) => await IssueAsync();
        revoke.Click += async (_, _) => await RevokeAsync();
        copyCode.Click += (_, _) => Copy(current?.Code);
        copyLink.Click += (_, _) => Copy(current is { Links.Count: > 0 } code ? code.Links[0] + "?code=" + code.Code : null);
        Shown += async (_, _) => await LoadCodeAsync();
        ShowCode();
    }

    private void SetBusy(bool value)
    {
        busy = value;
        issue.Enabled = revoke.Enabled = copyCode.Enabled = copyLink.Enabled = !value;
        duration.Enabled = !value;
        UseWaitCursor = value;
        if (!value) ShowCode();
    }

    private void ShowCode()
    {
        codeText.Text = current?.Code ?? "Код не выдан";
        expires.Text = current is null ? "Выберите срок действия и выдайте код." : $"Действует до {current.ExpiresAt:dd.MM.yyyy HH:mm}";
        links.Text = current is null ? "" : string.Join(Environment.NewLine, current.Links);
        issue.Text = current is null ? "Выдать код" : "Выдать новый код";
        revoke.Enabled = current is not null && !busy;
        copyCode.Enabled = current is not null && !busy;
        copyLink.Enabled = current is { Links.Count: > 0 } && !busy;
        var old = qr.Image;
        qr.Image = null;
        old?.Dispose();
        if (current is not { Links.Count: > 0 } code) return;
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(code.Links[0] + "?code=" + code.Code, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(8, [0, 0, 0, 255], [255, 255, 255, 255]);
        using var stream = new MemoryStream(png);
        using var image = Image.FromStream(stream);
        qr.Image = new Bitmap(image);
    }

    private async Task LoadCodeAsync()
    {
        SetBusy(true);
        error.Text = "";
        try { current = await api.EnrollmentCodeAsync(groupId); }
        catch (ApiException ex) { if (!IsDisposed) error.Text = ex.Message; }
        finally { if (!IsDisposed) SetBusy(false); }
    }

    private async Task IssueAsync()
    {
        if (busy || duration.SelectedIndex < 0) return;
        if (current is not null && !Ui.Confirm(this, "Выдать новый код", "Текущий код перестанет действовать.", "Выдать новый код")) return;
        SetBusy(true);
        error.Text = "";
        try { current = await api.CreateEnrollmentCodeAsync(groupId, Hours[duration.SelectedIndex]); }
        catch (ApiException ex) { if (!IsDisposed) error.Text = ex.Message; }
        finally { if (!IsDisposed) SetBusy(false); }
    }

    private async Task RevokeAsync()
    {
        if (busy || current is null || !Ui.Confirm(this, "Отозвать код", "Студенты больше не смогут отправить снимки по этому коду.", "Отозвать код", danger: true)) return;
        SetBusy(true);
        error.Text = "";
        try { await api.RevokeEnrollmentCodeAsync(groupId); current = null; }
        catch (ApiException ex) { if (!IsDisposed) error.Text = ex.Message; }
        finally { if (!IsDisposed) SetBusy(false); }
    }

    private void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); Ui.Toast(this, "Скопировано"); }
        catch (System.Runtime.InteropServices.ExternalException ex) { error.Text = ex.Message; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { qr.Image?.Dispose(); codeText.Font.Dispose(); }
        base.Dispose(disposing);
    }
}
