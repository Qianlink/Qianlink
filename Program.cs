using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<DeskStore>();
builder.Services.AddHostedService<AutoPromoService>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/state", (DeskStore s) => Results.Ok(s.GetState()));

app.MapPost("/api/rates", (DeskStore s, Dictionary<string, decimal> r) =>
{
    s.UpdateRates(r);
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/settings", (DeskStore s, AppSettings cfg) =>
{
    s.UpdateSettings(cfg);
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/upload-qr", async (HttpRequest req, IWebHostEnvironment env) =>
{
    if (!req.HasFormContentType) return Results.BadRequest();
    var form = await req.ReadFormAsync();
    var file = form.Files.GetFile("qr");
    if (file == null || file.Length == 0) return Results.BadRequest();
    var www = Path.Combine(env.ContentRootPath, "wwwroot");
    Directory.CreateDirectory(www);
    var path = Path.Combine(www, "my-qr.png");
    using var fs = new FileStream(path, FileMode.Create);
    await file.CopyToAsync(fs);
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/order", async (DeskStore s, OrderReq o) =>
{
    var ord = s.AddOrder(o);
    await s.NotifyTelegram($"🔔 <b>NEW ORDER #{ord.Id}</b>\n\n• Fund: <b>{ord.Fund}</b>\n• USDT: <b>{ord.Usdt} USDT</b>\n• Rate: ₹{ord.Rate}\n• Total INR: <b>₹{ord.Inr}</b>\n• Bank/UPI: <code>{ord.Bank}</code>\n• TxID: <code>{ord.TxId}</code>");
    return Results.Ok(new { ok = true, id = ord.Id });
});

app.MapPost("/api/order-status", (DeskStore s, StatusReq req) =>
{
    s.SetStatus(req.Id, req.Status);
    return Results.Ok(new { ok = true });
});

var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
app.Run($"http://0.0.0:{port}");

public class AppSettings
{
    public string Wallet { get; set; } = "TYourTrc20WalletAddressHere123456789";
    public string PromoText { get; set; } = "🚀 Best Live USDT ⇄ INR Exchange Rates! Instant INR Payout & 24/7 Desk Support.";
    public string ExchangeLink { get; set; } = "https://t.me";
    public string EnquiryLink { get; set; } = "https://t.me";
}

public class OrderItem
{
    public string Id { get; set; } = "";
    public string Fund { get; set; } = "";
    public decimal Usdt { get; set; }
    public decimal Rate { get; set; }
    public decimal Inr { get; set; }
    public string Bank { get; set; } = "";
    public string TxId { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public string Time { get; set; } = "";
}

public record OrderReq(string Fund, decimal Usdt, string Bank, string TxId);
public record StatusReq(string Id, string Status);

public class DeskStore
{
    public string BotToken => "8966497015:AAH32L6Yy4Gi7Oz6-zPH-IhSgKPuK-mbBdY";
    public string AdminChatId => "8956956503";
    public string PromoTarget => "8956956503";

    private readonly object _lock = new();
    private Dictionary<string, decimal> _rates = new()
    {
        ["gaming"] = 104m,
        ["stock"] = 117m,
        ["mix"] = 128m,
        ["pure"] = 145m
    };
    public AppSettings Settings { get; private set; } = new();
    private List<OrderItem> _orders = new();

    public object GetState()
    {
        lock (_lock) return new { rates = _rates, settings = Settings, orders = _orders };
    }

    public void UpdateRates(Dictionary<string, decimal> r)
    {
        lock (_lock)
        {
            foreach (var kv in r) if (_rates.ContainsKey(kv.Key)) _rates[kv.Key] = kv.Value;
        }
    }

    public void UpdateSettings(AppSettings cfg)
    {
        lock (_lock) Settings = cfg;
    }

    public OrderItem AddOrder(OrderReq o)
    {
        lock (_lock)
        {
            var rate = _rates.TryGetValue(o.Fund.ToLower(), out var val) ? val : 104m;
            var item = new OrderItem
            {
                Id = Random.Shared.Next(10000, 99999).ToString(),
                Fund = o.Fund.ToUpper(),
                Usdt = o.Usdt,
                Rate = rate,
                Inr = Math.Round(o.Usdt * rate, 2),
                Bank = o.Bank,
                TxId = o.TxId,
                Status = "Pending",
                Time = DateTime.UtcNow.AddHours(5.5).ToString("dd MMM HH:mm")
            };
            _orders.Insert(0, item);
            return item;
        }
    }

    public void SetStatus(string id, string st)
    {
        lock (_lock)
        {
            var x = _orders.FirstOrDefault(i => i.Id == id);
            if (x != null) x.Status = st;
        }
    }

    public async Task NotifyTelegram(string html)
    {
        try
        {
            using var c = new HttpClient();
            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("chat_id", AdminChatId),
                new KeyValuePair<string, string>("text", html),
                new KeyValuePair<string, string>("parse_mode", "HTML")
            });
            await c.PostAsync($"https://telegram.org{BotToken}/sendMessage", form);
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
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                dynamic st = _store.GetState();
                Dictionary<string, decimal> r = st.rates;
                AppSettings cfg = st.settings;
                string msg = $"🔥 <b>QIANLINK LIVE USDT ⇄ INR DESK</b>\n\n{cfg.PromoText}\n\n" +
                             $"🎮 Gaming Fund: <b>₹{r["gaming"]}/USDT</b>\n" +
                             $"📈 Stock Fund: <b>₹{r["stock"]}/USDT</b>\n" +
                             $"🔄 Mix Fund: <b>₹{r["mix"]}/USDT</b>\n" +
                             $"💎 Pure Fund: <b>₹{r["pure"]}/USDT</b>\n\n" +
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
                var resp = await c.PostAsync($"https://telegram.org{_store.BotToken}/sendMessage", form, stoppingToken);
                Console.WriteLine($"AUTO PROMO STATUS: {resp.StatusCode}");
            }
            catch (Exception ex) { Console.WriteLine($"AUTO PROMO ERR: {ex.Message}"); }
            await Task.Delay(TimeSpan.FromMinutes(20), stoppingToken);
        }
    }
}
