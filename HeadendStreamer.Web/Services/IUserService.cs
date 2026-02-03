using System.Collections.Generic;
using System.Threading.Tasks;
using HeadendStreamer.Web.Models;

namespace HeadendStreamer.Web.Services
{
    public interface IUserService
    {
        Task InitializeAsync();
        Task<User?> AuthenticateAsync(string username, string password);
        Task<IEnumerable<User>> GetAllUsersAsync();
        Task<User?> GetUserByIdAsync(int id);
        Task CreateUserAsync(string username, string password);
        Task UpdatePasswordAsync(int userId, string newPassword);
        Task DeleteUserAsync(int id);
    }
}
