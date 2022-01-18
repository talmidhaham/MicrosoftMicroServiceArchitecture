using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllersWithViews(options => options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()))
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    });


builder.Services.AddApplicationInsightsTelemetry(builder.Configuration);
builder.Services.AddApplicationInsightsKubernetesEnricher();

builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy())
    .AddUrlGroup(new Uri(builder.Configuration["IdentityUrlHC"]), name: "identityapi-check", tags: new string[] { "identityapi" });

builder.Services.Configure<AppSettings>(builder.Configuration);

if (builder.Configuration.GetValue<string>("IsClusterEnv") == bool.TrueString)
{
    builder.Services.AddDataProtection(opts =>
    {
        opts.ApplicationDiscriminator = "eshop.webnebularid";
    })
    .PersistKeysToStackExchangeRedis(ConnectionMultiplexer.Connect(builder.Configuration["DPConnectionString"]), "DataProtection-Keys");
}


// Add Antiforgery services and configure the header name that angular will use by default.
builder.Services.AddAntiforgery(options => options.HeaderName = "X-XSRF-TOKEN");

// Setup where the compiled version of our spa application will be, when in production. 
builder.Services.AddSpaStaticFiles(configuration =>
{
    configuration.RootPath = "wwwroot";
});

builder.Host.UseContentRoot(Directory.GetCurrentDirectory()).ConfigureAppConfiguration((builderContext, config) =>
{
    config.AddEnvironmentVariables();
}).ConfigureLogging((hostingContext, builder) =>
{
    builder.AddConfiguration(hostingContext.Configuration.GetSection("Logging"));
    builder.AddConsole();
    builder.AddDebug();
    builder.AddAzureWebAppDiagnostics();
})
        .UseSerilog((builderContext, config) =>
        {
            config
                .MinimumLevel.Information()
                .Enrich.FromLogContext()
                .WriteTo.Seq("http://seq")
                .ReadFrom.Configuration(builderContext.Configuration)
                .WriteTo.Console();
        });

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

//app.UseHttpsRedirection();
var antiforgery = app.Services.GetRequiredService<IAntiforgery>();

var LoggerFactory = app.Services.GetRequiredService<ILoggerFactory>();

var env = app.Services.GetRequiredService<IWebHostEnvironment>();

app.Use( (context, next) =>
{
    var path = context.Request.Path.Value;

    if (
        string.Equals(path, "/", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(path, "/index.html", StringComparison.OrdinalIgnoreCase))
    {
        // The request token has to be sent as a JavaScript-readable cookie, 
        // and Angular uses it by default.
        var tokens = antiforgery.GetAndStoreTokens(context);
        context.Response.Cookies.Append("XSRF-TOKEN", tokens.RequestToken,
            new CookieOptions() { HttpOnly = false });
    }

    return next(context);
});

//Seed Data
WebContextSeed.Seed(app, env, LoggerFactory);

var pathBase = builder.Configuration["PATH_BASE"];

if (!string.IsNullOrEmpty(pathBase))
{
    LoggerFactory.CreateLogger<Program>().LogDebug("Using PATH BASE '{pathBase}'", pathBase);
    app.UsePathBase(pathBase);
}
app.UseStaticFiles();
app.UseRouting();

app.UseEndpoints(endpoints =>
{
    endpoints.MapDefaultControllerRoute();
    endpoints.MapControllers();
    endpoints.MapHealthChecks("/liveness", new HealthCheckOptions
    {
        Predicate = r => r.Name.Contains("self")
    });
    endpoints.MapHealthChecks("/hc", new HealthCheckOptions()
    {
        Predicate = _ => true,
        ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
    });
});

// Handles all still unnatended (by any other middleware) requests by returning the default page of the SPA (wwwroot/index.html).
app.UseSpa(spa =>
{
    // To learn more about options for serving an Angular SPA from ASP.NET Core,
    // see https://go.microsoft.com/fwlink/?linkid=864501

    // the root of the angular app. (Where the package.json lives)
    spa.Options.SourcePath = "Client";

    //if (env.IsDevelopment())
    //{ 

    //    // use the SpaServices extension method for angular, that will make the application to run "ng serve" for us, when in development.
    //    spa.UseAngularCliServer(npmScript: "start");
    //}
});
app.MapControllerRoute(
    name: "default",
    pattern: "{controller}/{action=Index}/{id?}");

app.MapFallbackToFile("index.html"); ;

app.Run();
