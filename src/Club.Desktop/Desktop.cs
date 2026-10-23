using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Club.Protocol;

namespace Club.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "Клуб: ошибка");
        using var window = new MainWindow(args.Contains("--smoke-test"));
        Application.Run(window);
    }
}

internal sealed class Transport : IDisposable
{
#if ADMIN
    private readonly ApiClient api = new("http://127.0.0.1:5080");
#else
    private NamedPipeClientStream? pipe;
#endif
    public async Task<JsonElement> Request(string path, object? body = null)
    {
        var request = new RpcRequest(path, body is null ? null : JsonSerializer.Serialize(body, ApiClient.Json));
#if ADMIN
        if (body is not null) await api.Security();
        return ApiClient.Ensure(await api.Send(request));
#else
        if (pipe is null || !pipe.IsConnected)
        {
            pipe?.Dispose();
            pipe = new NamedPipeClientStream(".", PipeWire.Name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
            await pipe.ConnectAsync(3000);
            // A visitor must not be able to impersonate the service by creating the pipe first.
            var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner is null || !owner.IsWellKnown(WellKnownSidType.LocalSystemSid))
            {
                pipe.Dispose(); pipe = null;
                throw new UnauthorizedAccessException("Локальный канал не принадлежит службе SYSTEM.");
            }
        }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await PipeWire.Write(pipe, request, timeout.Token);
            return ApiClient.Ensure(await PipeWire.Read<RpcResponse>(pipe, timeout.Token));
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            pipe.Dispose(); pipe = null;
            throw new IOException("Соединение со службой потеряно. Войдите снова; неизвестную покупку повторяйте с прежним номером.", ex);
        }
#endif
    }
    public void Dispose()
    {
#if ADMIN
        api.Dispose();
#else
        pipe?.Dispose();
#endif
    }
}

internal sealed class MainWindow : Form
{
#if ADMIN
    private static readonly bool Staff = true;
#else
    private static readonly bool Staff = false;
#endif
    private readonly Transport transport = new();
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(1000, 0), Padding = new Padding(12) };
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel actions = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10), WrapContents = true };
    private readonly Dictionary<string, DataGridView> grids = [];
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 1000 };
    private readonly string pendingFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Club", Staff ? "admin-pending.json" : "visitor-pending.json");
    private JsonElement snapshot;
    private bool busy;
    private bool loggedIn;
    private string? warnedSession;
    private readonly bool smoke;

    public MainWindow(bool smoke)
    {
        this.smoke = smoke;
        Text = Staff ? "Клуб — администратор · 0.2 pilot" : "Клуб — посетитель · 0.2 pilot";
        Size = new Size(1120, 760); MinimumSize = new Size(780, 540); StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 11);
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 100, AutoScroll = true };
        bottom.Controls.Add(status);
        Controls.Add(tabs); Controls.Add(actions); Controls.Add(bottom);
        Button("Войти", Login);
        Button(Staff ? "Создать владельца" : "Регистрация", Register);
        Button("Обновить", RefreshState);
        Button("Повторить незавершённый запрос", RetryPending);
        Button("Выйти", async () => { await transport.Request("/api/logout", new { }); loggedIn = false; snapshot = default; foreach (var grid in grids.Values) grid.Rows.Clear(); status.Text = "Вы вышли из аккаунта."; });
        if (Staff)
        {
            Grid("computers", "Компьютеры", ("id", "ID"), ("name", "Место"), ("managed", "Привязка"), ("occupied", "Занято"), ("lastSeenMs", "Связь (UTC ms)"));
            Grid("customers", "Посетители", ("id", "ID"), ("username", "Логин"), ("balanceMinor", "Баланс, тыйын"));
            Button("Добавить ПК", AddComputer); Button("Пополнить", TopUp); Button("Тариф", Package);
            Button("Пауза", () => Session("pause")); Button("Продолжить", () => Session("resume")); Button("Завершить", () => Session("end"));
            Button("Бесплатное время", FreeSession); Button("Возврат", Refund); Button("Привязать ПК", BindDevice);
        }
        else Button("Купить выбранный пакет", Purchase);
        Grid("packages", "Пакеты", ("id", "ID"), ("name", "Название"), ("minutes", "Минуты"), ("priceMinor", "Цена, тыйын"), ("active", "Активен"));
        Grid("sessions", "Сеансы", ("id", "ID"), ("username", "Посетитель"), ("computerName", "ПК"), ("packageName", "Пакет"), ("remainingMs", "Остаток, мс"), ("status", "Состояние"), ("revision", "Команда"), ("ackRevision", "Подтверждение"), ("availableRefundMinor", "Доступный возврат, тыйын"));
        if (Staff)
        {
            Grid("audit", "Журнал", ("createdMs", "UTC ms"), ("actor", "Сотрудник"), ("action", "Операция"), ("details", "Описание"));
            var reportPage = new TabPage("Отчёт");
            reportPage.Controls.Add(new TextBox { Name = "report", Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both });
            tabs.TabPages.Add(reportPage);
        }
        Shown += async (_, _) =>
        {
            if (smoke)
            {
                var timer = new System.Windows.Forms.Timer { Interval = 500 };
                timer.Tick += (_, _) => { timer.Stop(); timer.Dispose(); Close(); };
                timer.Start(); return;
            }
#if ADMIN
            TryStartServer();
#endif
            status.Text = "Испытательная версия. Войдите в аккаунт. Суммы указаны в тыйынах: 100 = 1 сом.";
            if (File.Exists(pendingFile)) status.Text += " Есть запрос с неизвестным исходом: повторите его после входа.";
            await Task.CompletedTask;
            refresh.Start();
        };
        refresh.Tick += async (_, _) =>
        {
            if (busy || smoke) return;
            busy = true;
            try
            {
                if (!Staff) await LocalStatus();
                if (loggedIn && DateTime.UtcNow.Second % 5 == 0) await RefreshState();
            }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { busy = false; }
        };
        FormClosed += (_, _) => { refresh.Dispose(); transport.Dispose(); };
    }
#if ADMIN
    private void TryStartServer()
    {
        var exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Server", "Club.Server.exe"));
        if (File.Exists(exe))
        {
            // Server itself owns an exclusive database lock; do not terminate an existing server on app exit.
            using var connection = new System.Net.Sockets.TcpClient();
            try { connection.Connect("127.0.0.1", 5080); }
            catch (System.Net.Sockets.SocketException) { Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = true }); }
        }
    }
#endif
    private void Button(string title, Func<Task> action)
    {
        var button = new Button { Text = title, AutoSize = true, Margin = new Padding(4) };
        button.Click += async (_, _) =>
        {
            if (busy) return;
            busy = true; actions.Enabled = false;
            try { await action(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Клуб", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { busy = false; actions.Enabled = true; }
        };
        actions.Controls.Add(button);
    }
    private void Grid(string name, string title, params (string Key, string Caption)[] columns)
    {
        var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
            AllowUserToDeleteRows = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false };
        foreach (var (key, caption) in columns) grid.Columns.Add(key, caption);
        grid.Columns["id"]?.SetVisible(false);
        var page = new TabPage(title); page.Controls.Add(grid); tabs.TabPages.Add(page); grids[name] = grid;
    }
    private string Selected(string table, string key = "id") => grids[table].SelectedRows.Count == 1
        ? Convert.ToString(grids[table].SelectedRows[0].Cells[key].Value) ?? throw new InvalidOperationException("Выберите строку.")
        : throw new InvalidOperationException("Выберите строку на вкладке «" + grids[table].Parent!.Text + "».");
    private async Task Login()
    {
        var values = Ask("Вход", "Логин", "Пароль"); if (values is null) return;
        var result = await transport.Request("/api/login", new { username = values[0], password = values[1] });
        var role = result.GetProperty("role").GetString();
        if (Staff ? role is not ("Owner" or "Admin") : role != "Customer")
        { await transport.Request("/api/logout", new { }); throw new InvalidOperationException("Этот аккаунт предназначен для другого приложения."); }
        loggedIn = true; await RefreshState();
    }
    private async Task Register()
    {
        var values = Ask(Staff ? "Первоначальная настройка" : "Регистрация", "Логин", "Пароль", "Повтор пароля"); if (values is null) return;
        if (values[1] != values[2]) throw new InvalidOperationException("Пароли не совпадают.");
        await transport.Request(Staff ? "/api/setup/owner" : "/api/register", new { username = values[0], password = values[1], confirmPassword = values[2] });
        status.Text = "Аккаунт создан. Нажмите «Войти».";
    }
    private async Task RefreshState()
    {
        if (!loggedIn) return;
        snapshot = await transport.Request("/api/state");
        foreach (var (name, grid) in grids)
        {
            var selected = grid.SelectedRows.Count == 1 ? Convert.ToString(grid.SelectedRows[0].Cells[0].Value) : null;
            grid.Rows.Clear();
            foreach (var row in snapshot.GetProperty(name).EnumerateArray())
            {
                var values = grid.Columns.Cast<DataGridViewColumn>().Select(c => row.TryGetProperty(c.Name, out var value) ? value.ToString() : "").Cast<object>().ToArray();
                var index = grid.Rows.Add(values);
                if (Convert.ToString(values[0]) == selected) grid.Rows[index].Selected = true;
            }
        }
        if (Staff)
        {
            var text = tabs.TabPages.Cast<TabPage>().SelectMany(p => p.Controls.Cast<Control>()).OfType<TextBox>().Single();
            var report = snapshot.GetProperty("report");
            var lines = new List<string>();
            foreach (var (key, label) in new[] { ("topUpsMinor", "Пополнения"), ("purchasesMinor", "Покупки"),
                ("refundsMinor", "Выданные возвраты"), ("walletBalancesMinor", "Остатки на балансах"),
                ("cashNetMinor", "Наличные: получено минус возвраты"), ("transferNetMinor", "Переводы: получено минус возвраты") })
                lines.Add($"{label}: {report.GetProperty(key).GetInt64() / 100m:0.00} сом");
            lines.Add(""); lines.Add("Операции сотрудников за всё время:");
            foreach (var row in report.GetProperty("byEmployee").EnumerateArray())
                lines.Add($"{row.GetProperty("username")}: {(row.GetProperty("kind").GetString() == "TopUp" ? "Пополнение" : "Возврат")}, " +
                    $"{(row.GetProperty("method").GetString() == "Cash" ? "наличные" : "перевод")} — {row.GetProperty("amountMinor").GetInt64() / 100m:0.00} сом, операций: {row.GetProperty("operations")}");
            text.Text = string.Join(Environment.NewLine, lines);
        }
        var user = snapshot.GetProperty("user");
        status.Text = $"{user.GetProperty("username")} · Баланс: {user.GetProperty("balanceMinor").GetInt64() / 100m:0.00} сом. Команда выполнена, когда её номер совпал с подтверждением ПК.";
    }
    private async Task LocalStatus()
    {
        var result = await transport.Request("/local/status");
        var local = result.Deserialize<AgentStatus>(ApiClient.Json)!;
        var grant = local.Grant;
        status.Text = $"{local.Message} · {grant.State} · Осталось {TimeSpan.FromMilliseconds(grant.RemainingMs):hh\\:mm\\:ss}";
        if (grant.State == "Running" && grant.RemainingMs <= 300_000 && warnedSession != grant.SessionId)
        {
            warnedSession = grant.SessionId;
            System.Media.SystemSounds.Exclamation.Play();
            MessageBox.Show(this, "Осталось не более 5 минут. Сохраните игру: по окончании служба завершит сеанс Windows.", "Клуб — время заканчивается");
        }
    }
    private async Task Mutate(string path, object body)
    {
        if (!loggedIn) throw new InvalidOperationException("Сначала войдите.");
        if (File.Exists(pendingFile)) throw new InvalidOperationException("Сначала повторите незавершённый запрос. Новый номер может повторить списание.");
        var user = snapshot.GetProperty("user").GetProperty("id").GetString();
        var pending = new Pending(user!, path, JsonSerializer.Serialize(body, ApiClient.Json));
        Directory.CreateDirectory(Path.GetDirectoryName(pendingFile)!);
        using (var file = new FileStream(pendingFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(file, pending, ApiClient.Json);
            file.Flush(true);
        }
        await RetryPending();
    }
    private async Task RetryPending()
    {
        if (!loggedIn) throw new InvalidOperationException("Сначала войдите под аккаунтом, отправившим запрос.");
        if (!File.Exists(pendingFile)) { status.Text = "Незавершённых запросов нет."; return; }
        var pending = JsonSerializer.Deserialize<Pending>(File.ReadAllText(pendingFile), ApiClient.Json)!;
        if (snapshot.GetProperty("user").GetProperty("id").GetString() != pending.UserId)
            throw new InvalidOperationException("Запрос принадлежит другому аккаунту. Обратитесь к сотруднику.");
        try { await transport.Request(pending.Path, JsonSerializer.Deserialize<JsonElement>(pending.Json)); }
        catch (ApiException ex) when (ex.Status is 400 or 403 or 404 or 409)
        {
            // The server rejects these requests before committing the operation. Network/500/401 retain it.
            File.Delete(pendingFile);
            throw;
        }
        File.Delete(pendingFile);
        await RefreshState(); status.Text += " Запрос подтверждён.";
    }
    private Task AddComputer() { var v = Ask("Новое место", "Название"); return v is null ? Task.CompletedTask : Mutate("/api/admin/computers", new { name = v[0], operationId = Guid.NewGuid() }); }
    private Task TopUp() { var customer = Selected("customers"); var v = Ask("Пополнение выбранного посетителя", "Сумма в тыйынах", "Способ: Cash или Transfer"); return v is null ? Task.CompletedTask : Mutate("/api/admin/topups", new { customerId = customer, amountMinor = long.Parse(v[0]), method = v[1], operationId = Guid.NewGuid() }); }
    private Task Package() { var v = Ask("Новый пакет", "Название", "Минуты", "Цена в тыйынах"); return v is null ? Task.CompletedTask : Mutate("/api/admin/packages", new { name = v[0], minutes = int.Parse(v[1]), priceMinor = long.Parse(v[2]), active = true, operationId = Guid.NewGuid() }); }
    private Task Session(string action) { var id = Selected("sessions"); var v = Ask("Управление сеансом", "Причина"); return v is null ? Task.CompletedTask : Mutate($"/api/admin/sessions/{id}/{action}", new { reason = v[0], operationId = Guid.NewGuid() }); }
    private Task FreeSession() { var customer = Selected("customers"); var computer = Selected("computers"); var v = Ask("Бесплатное время для выбранных посетителя и ПК", "Минуты", "Причина"); return v is null ? Task.CompletedTask : Mutate("/api/admin/free-sessions", new { customerId = customer, computerId = computer, minutes = int.Parse(v[0]), reason = v[1], operationId = Guid.NewGuid() }); }
    private Task Refund() { var id = Selected("sessions"); var v = Ask("Ручной возврат за выбранный сеанс", "Сумма в тыйынах", "Способ: Cash или Transfer", "Причина"); return v is null ? Task.CompletedTask : Mutate("/api/admin/refunds", new { sessionId = id, amountMinor = long.Parse(v[0]), method = v[1], reason = v[2], operationId = Guid.NewGuid() }); }
    private Task Purchase() => Mutate("/api/purchases", new { packageId = Selected("packages"), operationId = Guid.NewGuid() });
    private async Task BindDevice()
    {
        if (!loggedIn) throw new InvalidOperationException("Сначала войдите.");
        var computer = Selected("computers");
        using var save = new SaveFileDialog { Filter = "Привязка ПК|*.binding.json", FileName = "PC.binding.json", OverwritePrompt = true };
        if (save.ShowDialog(this) != DialogResult.OK) return;
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var binding = new DeviceBinding(computer, token, Guid.NewGuid().ToString());
        // Export BEFORE sending: a lost response must not lose the only copy of the device key.
        File.WriteAllText(save.FileName, JsonSerializer.Serialize(binding, ApiClient.Json));
        await transport.Request("/api/admin/devices", binding);
        status.Text = "Привязка сохранена. Передайте файл установщику службы и удалите транспортную копию. При потере ответа повторите привязку из этого файла.";
    }
    private string[]? Ask(string title, params string[] labels)
    {
        using var dialog = new Form { Text = title, Width = 500, Height = 120 + labels.Length * 70, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false };
        var entries = new List<TextBox>();
        for (var i = 0; i < labels.Length; i++)
        {
            dialog.Controls.Add(new Label { Text = labels[i], Left = 15, Top = 15 + 70 * i, Width = 450 });
            var input = new TextBox { Left = 15, Top = 40 + 70 * i, Width = 450, UseSystemPasswordChar = labels[i].Contains("парол", StringComparison.OrdinalIgnoreCase) };
            entries.Add(input); dialog.Controls.Add(input);
        }
        var ok = new Button { Text = "Подтвердить", DialogResult = DialogResult.OK, Left = 300, Top = 20 + labels.Length * 70, Width = 165 };
        dialog.Controls.Add(ok); dialog.AcceptButton = ok;
        return dialog.ShowDialog(this) == DialogResult.OK ? entries.Select(x => x.Text.Trim()).ToArray() : null;
    }
    private sealed record Pending(string UserId, string Path, string Json);
}

internal static class GridExtensions
{
    public static void SetVisible(this DataGridViewColumn column, bool visible) => column.Visible = visible;
}
