using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Security;
using Stockia.Web.Services;


var builder = WebApplication.CreateBuilder(args);

// SR-34: every HTTPS endpoint accepts TLS 1.2 or later only.
builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(h => h.SslProtocols = TlsPolicy.Allowed));

builder.Services.AddDbContext<StockiaDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("StockiaDb")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IUserRepository, EfUserRepository>();
builder.Services.AddScoped<CompanyService>();
builder.Services.AddScoped<AssignmentService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<IJournalRepository, EfJournalRepository>();
builder.Services.AddScoped<ExchangeRateService>();
builder.Services.AddScoped<IExchangeRates>(sp => sp.GetRequiredService<ExchangeRateService>());
builder.Services.AddScoped<JournalService>();
builder.Services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();   // SR-34: bcrypt, work factor 12
builder.Services.AddScoped<LoginService>();
builder.Services.AddScoped<CompanyAccessService>();
builder.Services.AddScoped<CompanyAccessFilter>();
builder.Services.AddScoped<ItemService>();
builder.Services.AddScoped<WarehouseService>();
builder.Services.AddScoped<PurchaseInvoiceService>();
builder.Services.AddScoped<SalesInvoiceService>();
builder.Services.AddScoped<StockService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<JournalReversalService>();
// SR-4: automatic posting of documents. Invoice pages call PostingService.PostAsync when a document is saved;
// payments and receipts get their own IPostingRule later.
builder.Services.AddScoped<IPostedDocuments, EfPostedDocuments>();
builder.Services.AddScoped<IPostingRule, SalesInvoiceRule>();
builder.Services.AddScoped<IPostingRule, PurchaseInvoiceRule>();
builder.Services.AddScoped<PostingService>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Account/Login";
        o.AccessDeniedPath = "/Account/Login";
        // SR-34: the sign-in cookie travels over HTTPS only and page scripts cannot read it.
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
    });
builder.Services.AddAntiforgery(o =>
{
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.Cookie.HttpOnly = true;
});

// SR-34: browsers remember to use HTTPS only, for a year (sent outside Development).
builder.Services.AddHsts(o =>
{
    o.MaxAge = TimeSpan.FromDays(365);
    o.IncludeSubDomains = true;
});

// BR-4 / d5: every endpoint requires a signed-in user unless it is marked [AllowAnonymous].
// A new page is protected by default; nobody has to remember to add [Authorize].
builder.Services.AddAuthorization(o =>
    o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// SR-33 / d5: every page and API checks the chosen company and the user's role in it.
builder.Services.AddControllersWithViews(o => o.Filters.AddService<CompanyAccessFilter>(int.MinValue));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    DbSeeder.Seed(scope.ServiceProvider.GetRequiredService<StockiaDbContext>(),
                  scope.ServiceProvider.GetRequiredService<IPasswordHasher>());
}

// SR-34: HSTS outside Development (so localhost is never pinned), and every http request
// is redirected to https.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
app.UseHttpsRedirection();
app.UseStaticFiles();
// Amounts are typed as 1234.50 (type=number inputs): use one fixed culture so parsing never depends on the server's regional settings.
app.UseRequestLocalization(new RequestLocalizationOptions()
    .SetDefaultCulture("en-US").AddSupportedCultures("en-US").AddSupportedUICultures("en-US"));
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.Run();

public partial class Program { }
