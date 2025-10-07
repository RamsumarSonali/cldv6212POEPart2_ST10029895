
using Microsoft.AspNetCore.Mvc;
using ABCRetailers.Models;
using ABCRetailers.Models.ViewModels;
using ABCRetailers.Services;
using ABCRetailers.Constants;
using System.Text.Json;

namespace ABCRetailers.Controllers
{
    public class OrderController : Controller
    {
        private readonly IAzureStorageService _storageService;
        private readonly ILogger<OrderController> _logger;

        public OrderController(IAzureStorageService storageService, ILogger<OrderController> logger)
        {
            _storageService = storageService;
            _logger = logger;
        }

        public async Task<IActionResult> Index()
        {
            try
            {
                var orders = await _storageService.GetAllEntitiesAsync<Order>();
                return View(orders.OrderByDescending(o => o.OrderDate).ToList());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading orders");
                TempData["Error"] = "Error loading orders. Please try again.";
                return View(new List<Order>());
            }
        }

        public async Task<IActionResult> Create()
        {
            var customers = await _storageService.GetAllEntitiesAsync<Customer>();
            var products = await _storageService.GetAllEntitiesAsync<Product>();

            var viewModel = new OrderCreateViewModel
            {
                Customers = customers.Where(c => c.IsActive).ToList(),
                Products = products.Where(p => p.IsActive && p.StockAvailable > 0).ToList()
            };
            return View(viewModel);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(OrderCreateViewModel model)
        {
            if (ModelState.IsValid)
            {
                try
                {
                    var customer = await _storageService.GetEntityAsync<Customer>(
                        StorageConstants.CustomerPartitionKey, model.CustomerId);
                    var product = await _storageService.GetEntityAsync<Product>(
                        StorageConstants.ProductPartitionKey, model.ProductId);

                    if (customer == null || product == null)
                    {
                        ModelState.AddModelError("", "Invalid customer or product selected.");
                        await PopulateDropdowns(model);
                        return View(model);
                    }

                    // FIXED: Check stock with proper locking mechanism
                    if (product.StockAvailable < model.Quantity)
                    {
                        ModelState.AddModelError("Quantity", $"Insufficient stock. Available: {product.StockAvailable}");
                        await PopulateDropdowns(model);
                        return View(model);
                    }

                    // FIXED: Set RowKey explicitly
                    var order = new Order
                    {
                        RowKey = Guid.NewGuid().ToString(),
                        PartitionKey = StorageConstants.OrderPartitionKey,
                        CustomerId = model.CustomerId,
                        Username = customer.Username,
                        ProductId = model.ProductId,
                        ProductName = product.ProductName,
                        OrderDate = DateTime.UtcNow,
                        Quantity = model.Quantity,
                        UnitPrice = product.Price, // FIXED: Now uses decimal
                        TotalPrice = product.Price * model.Quantity,
                        Status = "Submitted"
                    };

                    await _storageService.AddEntityAsync(order);

                    // Update product stock
                    var previousStock = product.StockAvailable;
                    product.StockAvailable -= model.Quantity;
                    await _storageService.UpdateEntityAsync(product);

                    // Send queue message for new order
                    var orderMessage = new
                    {
                        OrderId = order.OrderId,
                        CustomerId = order.CustomerId,
                        CustomerName = $"{customer.Name} {customer.Surname}",
                        ProductName = product.ProductName,
                        Quantity = order.Quantity,
                        TotalPrice = order.TotalPrice,
                        OrderDate = DateTime.UtcNow,
                        Status = order.Status
                    };

                    await _storageService.SendMessageAsync(
                        StorageConstants.OrderNotificationsQueue,
                        JsonSerializer.Serialize(orderMessage));

                    // Send stock update message
                    var stockMessage = new
                    {
                        ProductId = product.ProductId,
                        ProductName = product.ProductName,
                        PreviousStock = previousStock,
                        NewStock = product.StockAvailable,
                        UpdatedBy = "Order System",
                        UpdateDate = DateTime.UtcNow
                    };

                    await _storageService.SendMessageAsync(
                        StorageConstants.StockUpdatesQueue,
                        JsonSerializer.Serialize(stockMessage));

                    TempData["Success"] = "Order created successfully!";
                    return RedirectToAction(nameof(Index));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error creating order");
                    ModelState.AddModelError("", $"Error creating order: {ex.Message}");
                }
            }

            await PopulateDropdowns(model);
            return View(model);
        }

        public async Task<IActionResult> Details(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return NotFound();
            }

            var order = await _storageService.GetEntityAsync<Order>(StorageConstants.OrderPartitionKey, id);
            if (order == null)
            {
                return NotFound();
            }

            return View(order);
        }

        public async Task<IActionResult> Edit(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return NotFound();
            }

            var order = await _storageService.GetEntityAsync<Order>(StorageConstants.OrderPartitionKey, id);
            if (order == null)
            {
                return NotFound();
            }

            // FIXED: Don't allow editing completed/cancelled orders
            if (order.Status == "Completed" || order.Status == "Cancelled")
            {
                TempData["Error"] = "Cannot edit completed or cancelled orders.";
                return RedirectToAction(nameof(Details), new { id });
            }

            return View(order);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(Order order)
        {
            if (ModelState.IsValid)
            {
                try
                {
                    var originalOrder = await _storageService.GetEntityAsync<Order>(
                        StorageConstants.OrderPartitionKey, order.RowKey);

                    if (originalOrder == null)
                    {
                        return NotFound();
                    }

                    // FIXED: Only allow status updates, not quantity changes
                    originalOrder.Status = order.Status;
                    originalOrder.TrackingNumber = order.TrackingNumber;

                    if (order.Status == "Shipped" && !originalOrder.ShippedDate.HasValue)
                    {
                        originalOrder.ShippedDate = DateTime.UtcNow;
                    }

                    if (order.Status == "Delivered" && !originalOrder.DeliveredDate.HasValue)
                    {
                        originalOrder.DeliveredDate = DateTime.UtcNow;
                    }

                    await _storageService.UpdateEntityAsync(originalOrder);
                    TempData["Success"] = "Order updated successfully!";
                    return RedirectToAction(nameof(Index));
                }
                catch (InvalidOperationException ex)
                {
                    ModelState.AddModelError("", ex.Message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error updating order");
                    ModelState.AddModelError("", "Error updating order. Please try again.");
                }
            }
            return View(order);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Cancel(string id)
        {
            try
            {
                var order = await _storageService.GetEntityAsync<Order>(StorageConstants.OrderPartitionKey, id);
                if (order == null)
                {
                    return NotFound();
                }

                if (order.Status == "Completed" || order.Status == "Cancelled")
                {
                    TempData["Error"] = "Cannot cancel completed or already cancelled orders.";
                    return RedirectToAction(nameof(Index));
                }

                // FIXED: Restore stock when cancelling
                var product = await _storageService.GetEntityAsync<Product>(
                    StorageConstants.ProductPartitionKey, order.ProductId);

                if (product != null)
                {
                    product.StockAvailable += order.Quantity;
                    await _storageService.UpdateEntityAsync(product);
                }

                order.Status = "Cancelled";
                await _storageService.UpdateEntityAsync(order);

                TempData["Success"] = "Order cancelled successfully!";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cancelling order");
                TempData["Error"] = $"Error cancelling order: {ex.Message}";
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public async Task<JsonResult> GetProductPrice(string productId)
        {
            try
            {
                var product = await _storageService.GetEntityAsync<Product>(
                    StorageConstants.ProductPartitionKey, productId);

                if (product != null)
                {
                    return Json(new
                    {
                        success = true,
                        price = product.Price,
                        stock = product.StockAvailable,
                        productName = product.ProductName
                    });
                }
                return Json(new { success = false });
            }
            catch
            {
                return Json(new { success = false });
            }
        }

        [HttpPost]
        public async Task<IActionResult> UpdateOrderStatus(string id, string newStatus)
        {
            try
            {
                var order = await _storageService.GetEntityAsync<Order>(StorageConstants.OrderPartitionKey, id);
                if (order == null)
                {
                    return Json(new { success = false, message = "Order not found" });
                }

                var previousStatus = order.Status;
                order.Status = newStatus;

                if (newStatus == "Shipped" && !order.ShippedDate.HasValue)
                {
                    order.ShippedDate = DateTime.UtcNow;
                }

                if (newStatus == "Delivered" && !order.DeliveredDate.HasValue)
                {
                    order.DeliveredDate = DateTime.UtcNow;
                }

                await _storageService.UpdateEntityAsync(order);

                // Send queue message for status update
                var statusMessage = new
                {
                    OrderId = order.OrderId,
                    CustomerId = order.CustomerId,
                    CustomerName = order.Username,
                    ProductName = order.ProductName,
                    PreviousStatus = previousStatus,
                    NewStatus = newStatus,
                    UpdatedDate = DateTime.UtcNow,
                    UpdatedBy = "System"
                };

                await _storageService.SendMessageAsync(
                    StorageConstants.OrderNotificationsQueue,
                    JsonSerializer.Serialize(statusMessage));

                return Json(new { success = true, message = $"Order status updated to {newStatus}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating order status");
                return Json(new { success = false, message = ex.Message });
            }
        }

        private async Task PopulateDropdowns(OrderCreateViewModel model)
        {
            model.Customers = await _storageService.GetAllEntitiesAsync<Customer>();
            model.Products = await _storageService.GetAllEntitiesAsync<Product>();
        }
    }
}