using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Dapper;
using HeadendStreamer.Web.Models;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Cryptography.KeyDerivation;

namespace HeadendStreamer.Web.Services
{
    public class UserService : IUserService
    {
        // Using SQLCipher (via bundle_e_sqlcipher) requires the Password keyword in the connection string.
        private const string ConnectionString = "Data Source=users.db;Password=simpfo@siti@2026;Mode=ReadWriteCreate;Cache=Shared";

        public UserService()
        {
            // Ensure SQLitePCL is initialized for proper provider usage (crucial for SQLCipher)
            // Note: In newer versions, this might be auto-init, but better safe.
            SQLitePCL.Batteries_V2.Init();
        }

        public async Task InitializeAsync()
        {
            using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();

            var sql = @"
                CREATE TABLE IF NOT EXISTS Users (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Username TEXT NOT NULL UNIQUE,
                    PasswordHash TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL
                );";
            
            await connection.ExecuteAsync(sql);

            // Create default admin user if no users exist
            var count = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Users");
            if (count == 0)
            {
                await CreateUserAsync("admin", "admin");
            }
        }

        public async Task<User?> AuthenticateAsync(string username, string password)
        {
            using var connection = new SqliteConnection(ConnectionString);
            var user = await connection.QuerySingleOrDefaultAsync<User>(
                "SELECT * FROM Users WHERE Username = @Username", new { Username = username });

            if (user == null)
                return null;

            if (VerifyPassword(password, user.PasswordHash))
            {
                return user;
            }

            return null;
        }

        public async Task<IEnumerable<User>> GetAllUsersAsync()
        {
            using var connection = new SqliteConnection(ConnectionString);
            return await connection.QueryAsync<User>("SELECT * FROM Users");
        }

        public async Task<User?> GetUserByIdAsync(int id)
        {
             using var connection = new SqliteConnection(ConnectionString);
             return await connection.QuerySingleOrDefaultAsync<User>("SELECT * FROM Users WHERE Id = @Id", new { Id = id });
        }

        public async Task CreateUserAsync(string username, string password)
        {
            var passwordHash = HashPassword(password);
            var user = new User
            {
                Username = username,
                PasswordHash = passwordHash,
                CreatedAt = DateTime.UtcNow
            };

            using var connection = new SqliteConnection(ConnectionString);
            await connection.ExecuteAsync(
                "INSERT INTO Users (Username, PasswordHash, CreatedAt) VALUES (@Username, @PasswordHash, @CreatedAt)",
                user);
        }

        public async Task UpdatePasswordAsync(int userId, string newPassword)
        {
             var passwordHash = HashPassword(newPassword);
             using var connection = new SqliteConnection(ConnectionString);
             await connection.ExecuteAsync("UPDATE Users SET PasswordHash = @PasswordHash WHERE Id = @Id", new { PasswordHash = passwordHash, Id = userId });
        }

        public async Task DeleteUserAsync(int id)
        {
            using var connection = new SqliteConnection(ConnectionString);
            await connection.ExecuteAsync("DELETE FROM Users WHERE Id = @Id", new { Id = id });
        }

        // --- Password Hashing Helpers (PBKDF2) ---

        private string HashPassword(string password)
        {
            // Generate a 128-bit salt using a cryptographically strong random sequence of nonzero values
            byte[] salt = new byte[128 / 8];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt);
            }

            // derive a 256-bit subkey (use HMACSHA256 with 100,000 iterations)
            string hashed = Convert.ToBase64String(KeyDerivation.Pbkdf2(
                password: password,
                salt: salt,
                prf: KeyDerivationPrf.HMACSHA256,
                iterationCount: 100000,
                numBytesRequested: 256 / 8));

            // format: {salt}.{hash}
            return $"{Convert.ToBase64String(salt)}.{hashed}";
        }

        private bool VerifyPassword(string password, string storedHash)
        {
            var parts = storedHash.Split('.');
            if (parts.Length != 2)
                return false;

            var salt = Convert.FromBase64String(parts[0]);
            var hash = parts[1];

            string hashedCheck = Convert.ToBase64String(KeyDerivation.Pbkdf2(
                password: password,
                salt: salt,
                prf: KeyDerivationPrf.HMACSHA256,
                iterationCount: 100000,
                numBytesRequested: 256 / 8));

            return hash == hashedCheck;
        }
    }
}
