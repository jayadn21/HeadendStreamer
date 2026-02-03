using HeadendStreamer.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HeadendStreamer.Web.Controllers
{
    [Authorize]
    public class UserController : Controller
    {
        private readonly IUserService _userService;

        public UserController(IUserService userService)
        {
            _userService = userService;
        }

        public async Task<IActionResult> Index()
        {
            if (User.Identity?.Name != "admin")
            {
                return RedirectToAction("Index", "Home");
            }

            var users = await _userService.GetAllUsersAsync();
            return View(users);
        }

        [HttpGet]
        public IActionResult Create()
        {
            if (User.Identity?.Name != "admin")
            {
                return RedirectToAction("Index", "Home");
            }
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> Create(string username, string password)
        {
            if (User.Identity?.Name != "admin")
            {
                return RedirectToAction("Index", "Home");
            }

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                ModelState.AddModelError("", "Username and password are required.");
                return View();
            }

            try
            {
                await _userService.CreateUserAsync(username, password);
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                // In a real app, check for unique constraint violation specifically
                ModelState.AddModelError("", "Error creating user. Username might already exist.");
                return View();
            }
        }

        [HttpPost]
        public async Task<IActionResult> Delete(int id)
        {
            if (User.Identity?.Name != "admin")
            {
                return RedirectToAction("Index", "Home");
            }

            // Prevent deleting self if needed, but for now simple
            // Ideally we check if it's the current user to avoid lockout, but requirements didn't specify.
            
            await _userService.DeleteUserAsync(id);
            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public IActionResult ChangePassword()
        {
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> ChangePassword(string currentPassword, string newPassword, string confirmPassword)
        {
            if (string.IsNullOrWhiteSpace(currentPassword) || string.IsNullOrWhiteSpace(newPassword))
            {
                ModelState.AddModelError("", "All fields are required.");
                return View();
            }

            if (newPassword != confirmPassword)
            {
                ModelState.AddModelError("", "New password and confirmation do not match.");
                return View();
            }

            // Get current user ID
            var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
            if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out int userId))
            {
                 return RedirectToAction("Login", "Auth");
            }

            // Verify current password first
            var user = await _userService.GetUserByIdAsync(userId);
            if (user == null)
            {
                return RedirectToAction("Login", "Auth");
            }

            // We need a way to verify the old password. 
            // UserService.AuthenticateAsync does this but returns the user object.
            // Since we already have the user, we should ideally have a verify method or just re-authenticate.
            var authUser = await _userService.AuthenticateAsync(user.Username, currentPassword);
            if (authUser == null)
            {
                ModelState.AddModelError("", "Incorrect current password.");
                return View();
            }

            await _userService.UpdatePasswordAsync(userId, newPassword);
            
            TempData["SuccessMessage"] = "Password changed successfully.";
            return RedirectToAction(nameof(Index));
        }
    }
}
