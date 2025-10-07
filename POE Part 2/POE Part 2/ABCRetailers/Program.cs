using ABCRetailers.Services;
using System.Globalization;

namespace ABCRetailers
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container
            builder.Services.AddControllersWithViews();

            // FIXED: Register Azure Storage Service with proper lifecycle
            builder.Services.AddSingleton<IAzureStorageService>(provider =>
            {
                var config = provider.GetRequiredService<IConfiguration>();
                var logger = provider.GetRequiredService<ILogger<AzureStorageService>>();
                var service = new AzureStorageService(config, logger);

                // Initialize asynchronously but don't block startup
                _ = service.InitializeStorageAsync();

                return service;
            });

            // Add logging
            builder.Services.AddLogging();

            // Add HttpClient for calling Azure Functions
            builder.Services.AddHttpClient("AzureFunctions", client =>
            {
                var baseUrl = builder.Configuration["AzureFunctions:BaseUrl"] ?? "http://localhost:7071/api";
                client.BaseAddress = new Uri(baseUrl);

                var functionKey = builder.Configuration["AzureFunctions:FunctionKey"];
                if (!string.IsNullOrEmpty(functionKey))
                {
                    client.DefaultRequestHeaders.Add("x-functions-key", functionKey);
                }
            });

            var app = builder.Build();

            // FIXED: Set culture for decimal handling
            var culture = new CultureInfo("en-US");
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;

            // Configure the HTTP request pipeline
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Home/Error");
                app.UseHsts();
            }

            app.UseHttpsRedirection();
            app.UseStaticFiles();
            app.UseRouting();
            app.UseAuthorization();

            app.MapControllerRoute(
                name: "default",
                pattern: "{controller=Home}/{action=Index}/{id?}");

            // FIXED: Ensure storage is initialized before running
            using (var scope = app.Services.CreateScope())
            {
                var storageService = scope.ServiceProvider.GetRequiredService<IAzureStorageService>();
                await storageService.InitializeStorageAsync();
            }

            await app.RunAsync();
        }
    }
}