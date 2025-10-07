using ABCRetailers.Models;
using ABCRetailers.Models.ViewModels;
using ABCRetailers.Services;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

namespace ABCRetailers.Controllers
{
    public class HomeController : Controller
    {
        private readonly IAzureStorageService _storageService;
        private readonly ILogger<HomeController> _logger;

        public HomeController(IAzureStorageService storageService, ILogger<HomeController> logger)
        {
            _storageService = storageService;
            _logger = logger;
        }

        public async Task<IActionResult> Index()
        {
            try
            {
                var products = await _storageService.GetAllEntitiesAsync<Product>();
                var customers = await _storageService.GetAllEntitiesAsync<Customer>();
                var orders = await _storageService.GetAllEntitiesAsync<Order>();

                var viewModel = new HomeViewModel
                {
                    FeaturedProducts = products.Where(p => p.IsActive)
                        .OrderByDescending(p => p.DateAdded)
                        .Take(5)
                        .ToList(),
                    ProductCount = products.Count(p => p.IsActive),
                    CustomerCount = customers.Count(c => c.IsActive),
                    OrderCount = orders.Count
                };

                return View(viewModel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading home page");
                return View(new HomeViewModel());
            }
        }

        public IActionResult Privacy()
        {
            return View();
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}