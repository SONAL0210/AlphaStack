using Serilog;
using AlphaStack.API.Middleware;
using AlphaStack.Application;
using AlphaStack.Infrastructure;
using AlphaStack.Application.Features.Trading;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting AlphaStack API");

    var builder = WebApplication.CreateBuilder(args);

    builder.Services.Configure<PaperTradingOptions>(
    builder.Configuration.GetSection("PaperTrading"));

    // ── Logging ───────────────────────────────────────────────────────────────
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .WriteTo.Console()
        .WriteTo.File("logs/trading-.log", rollingInterval: RollingInterval.Day));

    // ── Services ──────────────────────────────────────────────────────────────
    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);

    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new() { Title = "AlphaStack API", Version = "v1" });
    });


    // ── Pipeline ──────────────────────────────────────────────────────────────
    var app = builder.Build();

    /*if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }*/
    app.UseSwagger();
    app.UseSwaggerUI();

    app.UseGlobalExceptionHandler();
    app.UseSerilogRequestLogging();
    app.UseAuthorization();
    app.MapControllers();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
