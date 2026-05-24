using PlayCards.Hubs;
using PlayCards.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddSignalR();
builder.Services.AddSingleton<GameRoomService>();
builder.Services.AddSingleton<TurnRulesService>();
builder.Services.AddSingleton<AiMoveAdvisorService>();
builder.Services.AddSingleton<BotPlayerService>();
builder.Services.AddSingleton<SmartDefenseService>();
builder.Services.AddSingleton<ConnectionRecoveryService>();
builder.Services.AddHostedService<DisconnectedPlayerCleanupService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();

app.MapGet("/health", () => Results.Ok(new { status = "ok", app = "PlayCards" }));
app.MapBlazorHub();
app.MapHub<GameHub>("/gamehub");
app.MapFallbackToPage("/_Host");

app.Run();
