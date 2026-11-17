using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Services;


var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<StockiaDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("StockiaDb")));
builder.Services.AddScoped<IUserRepository, EfUserRepository>();
builder.Services.AddScoped<CompanyService>();
builder.Services.AddScoped<AssignmentService>();
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddScoped<LoginService>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o => { o.LoginPath = "/Account/Login"; o.AccessDeniedPath = "/Account/Login"; });

// BR-4 / d5: every endpoint requires a signed-in user unless it is marked [AllowAnonymous].
// A new page is protected by default; nobody has to remember to add [Authorize].
builder.Services.AddAuthorization(o =>
    o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddControllersWithViews();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    DbSeeder.Seed(scope.ServiceProvider.GetRequiredService<StockiaDbContext>(),
                  scope.ServiceProvider.GetRequiredService<IPasswordHasher>());
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.Run();

public partial class Program { }
