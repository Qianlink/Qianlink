using System.Net.Http.Headers;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<DeskStore>();
builder.Services.AddHostedService<AutoPromoService>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/state", (DeskStore s) => Results.Ok(s.GetPublicState()));

app.MapPost("/api/admin/login", (DeskStore s, PassReq req) =>
    s.CheckPassword(req.Password) ? Results.Ok(new { ok = true }) : Results.Unauthorized());

app.MapPost("/api/admin/password", (DeskStore s, ChangePassReq req) =>
{
    if (!s.CheckPassword(req.OldPassword) || string.IsNullOrWhiteSpace(req.NewPassword))
        return Results.BadRequest(new { ok = false });
    s.UpdatePassword(req.NewPassword.Trim());
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/admin/toggle-desk", (DeskStore s, ToggleReq req) =>
{
    if (!s.CheckPassword(req.Password)) return Results.Unauthorized();
    s.SetDeskOpen(req.IsOpen);
    return Results.Ok(new { ok = true, isOpen = s.IsDeskOpen });
});

app.MapPost("/api/admin/state", (DeskStore s, PassReq req) =>
    s.CheckPassword(req.Password) ? Results.Ok(s.GetAdminState()) : Results.Unauthorized());

app.MapPost("/api/rates", (DeskStore s, RatesUpdateReq req) =>
{
    if (!s.CheckPassword(req.Password)) return Results.Unauthorized();
    s.UpdateRatesAndMins(req.Rates, req.MinLimits);
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/settings", (DeskStore s, SettingsUpdateReq req) =>
{
    if (!s.CheckPassword(req.Password)) return Results.Unauthorized();
    s.UpdateSettings(req.Settings);
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/upload-qr", async (HttpRequest req, DeskStore s, IWebHostEnvironment env) =>
{
    if (!req.HasFormContentType) return Results.BadRequest();
    var form = await req.ReadFormAsync();
    if (!s.CheckPassword(form["password"].ToString())) return Results.Unauthorized();
    var file = form.Files.GetFile("qr");
    if (file == null || file.Length == 0) return Results.BadRequest();
    var www = Path.Combine(env.ContentRootPath, "wwwroot");
    Directory.CreateDirectory(www);
    var ext = Path.GetExtension(file.FileName).ToLower();
    if (ext != ".jpg" && ext != ".jpeg" && ext != ".png" && ext != ".webp") ext = ".jpg";
    var fname = "custom-qr" + ext;
    using var fs = new FileStream(Path.Combine(www, fname), FileMode.Create);
    await file.CopyToAsync(fs);
    s.SetQrUrl("/" + fname);
    return Results.Ok(new { ok = true, url = "/" + fname });
});

app.MapPost("/api/order", async (HttpRequest req, DeskStore s, IWebHostEnvironment env) =>
{
    if (!s.IsDeskOpen) return Results.BadRequest(new { ok = false, msg = "Desk is currently closed!" });
    if (!req.HasFormContentType) return Results.BadRequest();
    var form = await req.ReadFormAsync();
    var fund = form["fund"].ToString().ToLower();
    decimal.TryParse(form["usdt"].ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var usdt);
    var holder = form["holder"].ToString().Trim();
    var accNo = form["accNo"].ToString().Trim();
    var ifsc = form["ifsc"].ToString().Trim().ToUpper();
    var receipt = form.Files.GetFile("receipt");

    if (receipt == null || receipt.Length == 0 || !s.ValidateOrder(fund, usdt, holder, accNo, ifsc))
        return Results.BadRequest(new { ok = false, msg = "Invalid order or minimum USDT condition not met." });

    var recDir = Path.Combine(env.ContentRootPath, "wwwroot", "receipts");
    Directory.CreateDirectory(recDir);
    var token = "QL-" + Random.Shared.Next(100000, 999999);
    var ext = Path.GetExtension(receipt.FileName);
    if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";
    var fileName = $"{token}{ext}";
    var fullPath = Path.Combine(recDir, fileName);

    using (var fs = new FileStream(fullPath, FileMode.Create))
        await receipt.CopyToAsync(fs);

    var ord = s.AddOrder(token, fund, usdt, holder, accNo, ifsc, $"/receipts/{fileName}", fullPath);
    await s.NotifyTelegramWithReceipt(ord, fullPath);
    return Results.Ok(new { ok = true, token = ord.Token, inr = ord.Inr });
});

app.MapPost("/api/order-status", (DeskStore s, StatusReq req) =>
{
    if (!s.CheckPassword(req.Password)) return Results.Unauthorized();
    s.SetStatus(req.Token, req.Status);
    return Results.Ok(new { ok = true });
});

var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
app.Run($"http://0.0.0:{port}");
public record PassReq(string Password);
public record ChangePassReq(string OldPassword, string NewPassword);
public record ToggleReq(string Password, bool IsOpen);
public record RatesUpdateReq(string Password, Dictionary<string, decimal> Rates, Dictionary<string, decimal> MinLimits);
public record SettingsUpdateReq(string Password, AppSettings Settings);
public record StatusReq(string Password, string Token, string Status);

public class AppSettings
{
    public string Wallet { get; set; } = "TBMdnk1sTcAxSjb9b129cvkRs6eb9TQzgt";
    public string PromoText { get; set; } = "🚀 Best Live USDT ⇄ INR Exchange Rates! Instant INR Payout & 24/7 Desk Support.";
    public string ExchangeLink { get; set; } = "https://t.me";
    public string EnquiryLink { get; set; } = "https://t.me";
}

public class OrderItem
{
    public string Token { get; set; } = "";
    public string Fund { get; set; } = "";
    public decimal Usdt { get; set; }
    public decimal Rate { get; set; }
    public decimal Inr { get; set; }
    public string Holder { get; set; } = "";
    public string AccNo { get; set; } = "";
    public string Ifsc { get; set; } = "";
    public string ReceiptUrl { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public DateTime CreatedUtc { get; set; }
    public string TimeIst { get; set; } = "";
}

public class DeskStore
{
    public string BotToken => "8966497015:AAH32L6Yy4Gi7Oz6-zPH-IhSgKPuK-mbBdY";
    public string AdminChatId => "8956956503";
    public string PromoTarget => "8956956503";

    private readonly object _lock = new();
    private string _adminPass = "SAMAR@0219";
    public bool IsDeskOpen { get; private set; } = true;
    public string QrUrl { get; private set; } = "";
    private Dictionary<string, decimal> _rates = new() { ["gaming"] = 104m, ["stock"] = 117m, ["mix"] = 128m, ["pure"] = 145m };
    private Dictionary<string, decimal> _mins = new() { ["gaming"] = 1000m, ["stock"] = 1500m, ["mix"] = 2000m, ["pure"] = 2500m };
    public AppSettings Settings { get; private set; } = new();
    private List<OrderItem> _orders = new();

    public bool CheckPassword(string p) { lock (_lock) return p == _adminPass; }
    public void UpdatePassword(string np) { lock (_lock) _adminPass = np; }
    public void SetDeskOpen(bool open) { lock (_lock) IsDeskOpen = open; }
    public void SetQrUrl(string u) { lock (_lock) QrUrl = u; }

    public void CleanupOldRecords()
    {
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow.AddDays(-3);
            var expired = _orders.Where(o => o.CreatedUtc < cutoff).ToList();
            foreach (var o in expired)
            {
                try { if (!string.IsNullOrEmpty(o.FilePath) && File.Exists(o.FilePath)) File.Delete(o.FilePath); } catch { }
            }
            _orders.RemoveAll(o => o.CreatedUtc < cutoff);
        }
    }

    public object GetPublicState()
    {
        CleanupOldRecords();
        lock (_lock) return new { isOpen = IsDeskOpen, rates = _rates, minLimits = _mins, settings = Settings, qrUrl = QrUrl };
    }

    public object GetAdminState()
    {
        CleanupOldRecords();
        lock (_lock) return new { isOpen = IsDeskOpen, rates = _rates, minLimits = _mins, settings = Settings, orders = _orders, qrUrl = QrUrl };
    }

    public void UpdateRatesAndMins(Dictionary<string, decimal> r, Dictionary<string, decimal> m)
    {
        lock (_lock)
        {
            foreach (var kv in r) if (_rates.ContainsKey(kv.Key)) _rates[kv.Key] = kv.Value;
            foreach (var kv in m) if (_mins.ContainsKey(kv.Key)) _mins[kv.Key] = kv.Value;
        }
    }

    public void UpdateSettings(AppSettings cfg) { lock (_lock) Settings = cfg; }

    public bool ValidateOrder(string fund, decimal usdt, string holder, string accNo, string ifsc)
    {
        lock (_lock)
        {
            if (!_rates.ContainsKey(fund) || !_mins.ContainsKey(fund)) return false;
            if (usdt < _mins[fund]) return false;
            return !string.IsNullOrWhiteSpace(holder) && !string.IsNullOrWhiteSpace(accNo) && !string.IsNullOrWhiteSpace(ifsc);
        }
    }

    public OrderItem AddOrder(string token, string fund, decimal usdt, string holder, string accNo, string ifsc, string recUrl, string filePath)
    {
        CleanupOldRecords();
        lock (_lock)
        {
            var rate = _rates[fund];
            var now = DateTime.UtcNow;
            var displayFund = fund == "pure" ? "RATHAT" : fund.ToUpper();
            var item = new OrderItem
            {
                Token = token,
                Fund = displayFund,
                Usdt = usdt,
                Rate = rate,
                Inr = Math.Round(usdt * rate, 2),
                Holder = holder,
                AccNo = accNo,
                Ifsc = ifsc,
                ReceiptUrl = recUrl,
                FilePath = filePath,
                Status = "Pending",
                CreatedUtc = now,
                TimeIst = now.AddHours(5.5).ToString("dd MMM yyyy, hh:mm tt")
            };
            _orders.Insert(0, item);
            return item;
        }
    }

    public void SetStatus(string token, string st)
    {
        lock (_lock)
        {
            var x = _orders.FirstOrDefault(i => i.Token == token);
            if (x != null) x.Status = st;
        }
    }

    public async Task NotifyTelegramWithReceipt(OrderItem o, string filePath)
    {
        try
        {
            using var c = new HttpClient();
            string apiBase = "https://" + "api.telegram.org" + "/bot" + BotToken;
            string msg = $"🔔 <b>NEW DEPOSIT ORDER ({o.Token})</b>\n\n" +
                         $"📂 Fund: <b>{o.Fund} FUNDS</b>\n" +
                         $"💵 USDT: <b>{o.Usdt} USDT</b> @ ₹{o.Rate}\n" +
                         $"💰 Total INR: <b>₹{o.Inr:N2}</b>\n\n" +
                         $"🏦 <b>Bank Payout Details:</b>\n" +
                         $"• Name: <code>{o.Holder}</code>\n" +
                         $"• A/C No: <code>{o.AccNo}</code>\n" +
                         $"• IFSC: <code>{o.Ifsc}</code>\n\n" +
                         $"🕒 Time: {o.TimeIst}";

            var txtForm = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("chat_id", AdminChatId),
                new KeyValuePair<string, string>("text", msg),
                new KeyValuePair<string, string>("parse_mode", "HTML")
            });
            await c.PostAsync(apiBase + "/sendMessage", txtForm);

            using var mp = new MultipartFormDataContent();
            mp.Add(new StringContent(AdminChatId), "chat_id");
            mp.Add(new StringContent($"🧾 Payment Receipt ({o.Token})"), "caption");
            var fileBytes = await File.ReadAllBytesAsync(filePath);
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
            mp.Add(fileContent, "document", Path.GetFileName(filePath));

            await c.PostAsync(apiBase + "/sendDocument", mp);
        }
        catch { }
    }
}

public class AutoPromoService : BackgroundService
{
    private readonly DeskStore _store;
    public AutoPromoService(DeskStore store) { _store = store; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(10000, stoppingToken);
        using var c = new HttpClient();
        string apiBase = "https://" + "api.telegram.org" + "/bot" + _store.BotToken;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_store.IsDeskOpen)
                {
                    _store.CleanupOldRecords();
                    dynamic st = _store.GetPublicState();
                    Dictionary<string, decimal> r = st.rates;
                    Dictionary<string, decimal> m = st.minLimits;
                    AppSettings cfg = st.settings;
                    string msg = $"🔥 <b>QIANLINK LIVE USDT ⇄ INR DESK</b>\n\n{cfg.PromoText}\n\n" +
                                 $"🎮 Gaming Fund: <b>₹{r["gaming"]}</b> (Min {m["gaming"]} USDT)\n" +
                                 $"📈 Stock Fund: <b>₹{r["stock"]}</b> (Min {m["stock"]} USDT)\n" +
                                 $"🔄 Mix Fund: <b>₹{r["mix"]}</b> (Min {m["mix"]} USDT)\n" +
                                 $"💎 Rathat Fund: <b>₹{r["pure"]}</b> (Min {m["pure"]} USDT)\n\n" +
                                 $"⚡ Instant INR Settlement | 100% Safe Desk";
                    var kb = new
                    {
                        inline_keyboard = new[]
                        {
                            new[] { new { text = "💱 Start USDT Work / Exchange", url = cfg.ExchangeLink } },
                            new[] { new { text = "💬 Any Enquiry / Live Support", url = cfg.EnquiryLink } }
                        }
                    };
                    var form = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("chat_id", _store.PromoTarget),
                        new KeyValuePair<string, string>("text", msg),
                        new KeyValuePair<string, string>("parse_mode", "HTML"),
                        new KeyValuePair<string, string>("reply_markup", JsonSerializer.Serialize(kb))
                    });
                    await c.PostAsync(apiBase + "/sendMessage", form, stoppingToken);
                }
            }
            catch { }
            await Task.Delay(TimeSpan.FromMinutes(20), stoppingToken);
        }
    }
}
