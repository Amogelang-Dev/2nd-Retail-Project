using System.Diagnostics;
using Chabis_Hotness_PTY_Ltd.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration; // Required for configuration
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using System.Threading.Tasks; // Required for async/await
using PayStack.Net; // Use this for Paystack integration
using System;

namespace Chabis_Hotness_PTY_Ltd.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly IConfiguration _configuration;
        private readonly PayStackApi _paystackApi;
        private const string CartSessionKey = "ShoppingCart";

        // Constructor updated to include IConfiguration and initialize Paystack
        public HomeController(ILogger<HomeController> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;

            // Initialize Paystack API using the Secret Key from configuration
            string paystackSecretKey = _configuration["Paystack:SecretKey"];

            if (string.IsNullOrEmpty(paystackSecretKey))
            {
                // Critical alert if the key is missing in configuration
                _logger.LogCritical("Paystack Secret Key is missing from appsettings.json. Payment verification will fail.");
            }

            // PayStackApi handles communication with the Paystack servers
            _paystackApi = new PayStackApi(paystackSecretKey);
        }

        // --- Standard Navigation Actions ---
        public IActionResult Index() => View();
        public IActionResult About() => View();
        public IActionResult Contact() => View();
        public IActionResult Products() => View();
        public IActionResult Shop() => View();

        // Display the Cart page
        public IActionResult Cart()
        {
            var cart = GetCart();
            return View(cart);
        }

        // --- Cart Management Actions ---

        // Add item to cart
        [HttpPost]
        public IActionResult AddItemToCart([FromBody] CartItem newItem)
        {
            if (newItem == null)
                return BadRequest(new { success = false, message = "Invalid item." });

            var cart = GetCart();
            var existingItem = cart.FirstOrDefault(i => i.ProductId == newItem.ProductId && i.Size == newItem.Size);

            if (existingItem != null)
            {
                existingItem.Quantity += newItem.Quantity > 0 ? newItem.Quantity : 1;
            }
            else
            {
                newItem.Quantity = newItem.Quantity > 0 ? newItem.Quantity : 1;
                cart.Add(newItem);
            }

            SaveCart(cart);

            return Ok(new
            {
                success = true,
                count = cart.Sum(c => c.Quantity)
            });
        }

        // Remove an item from the cart
        [HttpPost]
        public IActionResult RemoveFromCart([FromBody] CartItem removeItem)
        {
            var cart = GetCart();
            var itemToRemove = cart.FirstOrDefault(i => i.ProductId == removeItem.ProductId && i.Size == removeItem.Size);
            if (itemToRemove != null)
            {
                cart.Remove(itemToRemove);
                SaveCart(cart);
            }

            return Ok(new { success = true, count = cart.Sum(c => c.Quantity) });
        }

        // Update quantity of an item in the cart
        [HttpPost]
        public IActionResult UpdateCartQuantity([FromBody] CartItem updateItem)
        {
            var cart = GetCart();
            var item = cart.FirstOrDefault(i => i.ProductId == updateItem.ProductId && i.Size == updateItem.Size);
            if (item != null)
            {
                item.Quantity = updateItem.Quantity > 0 ? updateItem.Quantity : 1;
                SaveCart(cart);

                var itemTotal = item.Quantity * item.Price;
                var cartTotal = cart.Sum(c => c.Price * c.Quantity) + 30; // delivery fee

                return Ok(new { success = true, itemTotal, cartTotal });
            }

            return Ok(new { success = false });
        }

        // --- Payment Integration Actions ---

        // Checkout page: Prepares payment variables for the client-side
        public IActionResult Checkout()
        {
            var cart = GetCart();
            if (!cart.Any())
                return RedirectToAction("Cart");

            decimal subtotal = cart.Sum(c => c.Price * c.Quantity);
            decimal deliveryFee = 30;
            decimal total = subtotal + deliveryFee;

            // Paystack requires the total amount in the smallest unit (cents/kobo)
            long amountInKobo = (long)Math.Round(total * 100M);

            // Placeholder for customer email (should be obtained from login/form)
            string customerEmail = "customer@example.com";

            // Retrieve the Paystack Public Key for the client-side widget
            string paystackPublicKey = _configuration["Paystack:PublicKey"];

            if (string.IsNullOrEmpty(paystackPublicKey))
            {
                _logger.LogError("Paystack Public Key is missing from configuration (appsettings.json).");
            }

            ViewBag.PaystackPublicKey = paystackPublicKey;
            ViewBag.TotalAmount = total;
            ViewBag.CustomerEmail = customerEmail;

            return View(cart);
        }

        // This action handles the Paystack redirect and server-side verification
        public IActionResult PaymentCallback(string reference)
        {
            if (string.IsNullOrEmpty(reference))
            {
                ViewBag.Message = "Payment reference is missing.";
                return View("PaymentFailure");
            }

            // Verify the transaction using the Paystack SDK
            TransactionVerifyResponse verificationResponse = null;
            try
            {
                verificationResponse = _paystackApi.Transactions.Verify(reference);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Paystack verification API call failed.");
                ViewBag.Message = "An error occurred during payment verification. Please contact support.";
                return View("PaymentFailure");
            }


            if (verificationResponse.Status && verificationResponse.Data.Status == "success")
            {
                // CRITICAL: You must implement a check here to ensure:
                // 1. The verified amount (verificationResponse.Data.Amount / 100) matches the expected total.
                // 2. The transaction reference hasn't been used before.

                // FULFILL THE ORDER HERE (Save order details to database)

                // Redirect to the success page
                return RedirectToAction("PaymentSuccess", "Home", new { orderRef = reference });
            }
            else
            {
                // Handle payment not successful or verification failure
                string message = verificationResponse.Status
                    ? $"Payment not successful. Status: {verificationResponse.Data.GatewayResponse}"
                    : $"Verification failed: {verificationResponse.Message}";

                ViewBag.Message = message;
                return View("PaymentFailure");
            }
        }

        // Payment success page
        [HttpGet] // Added to resolve AmbiguousMatchException
        public IActionResult PaymentSuccess(string orderRef)
        {
            // Clear cart after successful payment
            SaveCart(new List<CartItem>());
            ViewBag.OrderReference = orderRef;
            return View();
        }

        // Payment failure page
        public IActionResult PaymentFailure()
        {
            // Message will be populated by PaymentCallback if verification failed
            return View();
        }

        // --- Helper Methods ---

        // Helper: Get cart from session
        private List<CartItem> GetCart()
        {
            var cartJson = HttpContext.Session.GetString(CartSessionKey);
            if (string.IsNullOrEmpty(cartJson))
                return new List<CartItem>();

            try
            {
                return JsonConvert.DeserializeObject<List<CartItem>>(cartJson);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Failed to deserialize cart session data.");
                return new List<CartItem>();
            }
        }

        // Helper: Save cart to session
        private void SaveCart(List<CartItem> cart)
        {
            var cartJson = JsonConvert.SerializeObject(cart);
            HttpContext.Session.SetString(CartSessionKey, cartJson);
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}