using Microsoft.Data.Sqlite;
using SQLitePCL;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using VisionInspection.Core.Models;

namespace VisionInspection.Core.Services;

/// <summary>
/// 用户管理服务
/// </summary>
public class UserManager
{
    private readonly string _connectionString;
    private User? _currentUser;

    public UserManager(string dbPath)
    {
        // 初始化SQLitePCL
        Batteries_V2.Init();
        
        _connectionString = $"Data Source={dbPath}";
        InitializeDatabase();
    }

    /// <summary>
    /// 当前登录用户
    /// </summary>
    public User? CurrentUser => _currentUser;

    /// <summary>
    /// 是否已登录
    /// </summary>
    public bool IsLoggedIn => _currentUser != null;

    /// <summary>
    /// 当前用户角色
    /// </summary>
    public UserRole CurrentRole => _currentUser?.Role ?? UserRole.Guest;

    /// <summary>
    /// 初始化数据库表
    /// </summary>
    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // 创建用户表
        var createUserTable = @"
            CREATE TABLE IF NOT EXISTS Users (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Username TEXT UNIQUE NOT NULL,
                DisplayName TEXT NOT NULL,
                PasswordHash TEXT NOT NULL,
                Role INTEGER NOT NULL DEFAULT 1,
                IsActive INTEGER NOT NULL DEFAULT 1,
                CreatedAt TEXT NOT NULL,
                LastLoginAt TEXT,
                LastLoginIp TEXT
            )";

        // 创建登录记录表
        var createLoginRecordTable = @"
            CREATE TABLE IF NOT EXISTS LoginRecords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId INTEGER NOT NULL,
                Username TEXT NOT NULL,
                LoginTime TEXT NOT NULL,
                LogoutTime TEXT,
                LoginIp TEXT,
                IsSuccess INTEGER NOT NULL DEFAULT 1,
                FailReason TEXT
            )";

        using (var cmd = new SqliteCommand(createUserTable, connection))
        {
            cmd.ExecuteNonQuery();
        }

        using (var cmd = new SqliteCommand(createLoginRecordTable, connection))
        {
            cmd.ExecuteNonQuery();
        }

        // 检查是否需要创建默认管理员账户
        CreateDefaultAdminIfNeeded(connection);
    }

    /// <summary>
    /// 创建默认管理员账户
    /// </summary>
    private void CreateDefaultAdminIfNeeded(SqliteConnection connection)
    {
        using var cmd = new SqliteCommand("SELECT COUNT(*) FROM Users", connection);
        var count = Convert.ToInt32(cmd.ExecuteScalar());

        if (count == 0)
        {
            // 创建默认管理员账户
            var admin = new User
            {
                Username = "admin",
                DisplayName = "系统管理员",
                Role = UserRole.Admin,
                IsActive = true,
                CreatedAt = DateTime.Now
            };
            admin.SetPassword("admin123");

            InsertUserInternal(connection, admin);

            // 创建默认操作员账户
            var operator1 = new User
            {
                Username = "operator",
                DisplayName = "操作员",
                Role = UserRole.Operator,
                IsActive = true,
                CreatedAt = DateTime.Now
            };
            operator1.SetPassword("operator123");

            InsertUserInternal(connection, operator1);
        }
    }

    /// <summary>
    /// 用户登录
    /// </summary>
    public Task<(bool Success, string Message)> LoginAsync(string username, string password, string ipAddress = "")
    {
        // 使用Task.Run在后台线程执行，避免WPF死锁
        return Task.Run(() =>
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                // 查询用户
                using var cmd = new SqliteCommand(
                    "SELECT * FROM Users WHERE Username = @Username", connection);
                cmd.Parameters.AddWithValue("@Username", username);

                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                {
                    RecordLoginSync(0, username, false, ipAddress, "用户名不存在");
                    return (false, "用户名或密码错误");
                }

                var user = MapUserFromReader(reader);

                // 检查账户是否启用
                if (!user.IsActive)
                {
                    RecordLoginSync(user.Id, username, false, ipAddress, "账户已禁用");
                    return (false, "账户已禁用，请联系管理员");
                }

                // 验证密码
                if (!user.VerifyPassword(password))
                {
                    RecordLoginSync(user.Id, username, false, ipAddress, "密码错误");
                    return (false, "用户名或密码错误");
                }

                // 更新最后登录信息
                user.LastLoginAt = DateTime.Now;
                user.LastLoginIp = ipAddress;
                UpdateLastLoginSync(connection, user);

                // 记录登录成功
                RecordLoginSync(user.Id, username, true, ipAddress, "");

                _currentUser = user;
                return (true, $"欢迎，{user.DisplayName}");
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                return (false, $"数据库错误：{ex.Message} (错误码: {ex.SqliteErrorCode})");
            }
            catch (Exception ex)
            {
                return (false, $"登录失败：{ex.Message}");
            }
        });
    }

    /// <summary>
    /// 用户登出
    /// </summary>
    public void Logout()
    {
        if (_currentUser != null)
        {
            // 更新登出时间
            UpdateLogoutTime(_currentUser.Id);
            _currentUser = null;
        }
    }

    /// <summary>
    /// 检查当前用户是否有权限
    /// </summary>
    public bool HasPermission(string permission)
    {
        return Permissions.HasPermission(CurrentRole, permission);
    }

    /// <summary>
    /// 获取所有用户
    /// </summary>
    public async Task<List<User>> GetAllUsersAsync()
    {
        var users = new List<User>();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = new SqliteCommand("SELECT * FROM Users ORDER BY Id", connection);
        using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            users.Add(MapUserFromReader(reader));
        }

        return users;
    }

    /// <summary>
    /// 添加用户
    /// </summary>
    public async Task<(bool Success, string Message)> AddUserAsync(User user, string password)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            // 检查用户名是否已存在
            using (var checkCmd = new SqliteCommand(
                "SELECT COUNT(*) FROM Users WHERE Username = @Username", connection))
            {
                checkCmd.Parameters.AddWithValue("@Username", user.Username);
                var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());
                if (count > 0)
                {
                    return (false, "用户名已存在");
                }
            }

            user.SetPassword(password);
            user.CreatedAt = DateTime.Now;

            InsertUserInternal(connection, user);
            return (true, "用户创建成功");
        }
        catch (Exception ex)
        {
            return (false, $"创建用户失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 更新用户
    /// </summary>
    public async Task<(bool Success, string Message)> UpdateUserAsync(User user)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var cmd = new SqliteCommand(@"
                UPDATE Users SET 
                    DisplayName = @DisplayName,
                    Role = @Role,
                    IsActive = @IsActive
                WHERE Id = @Id", connection);

            cmd.Parameters.AddWithValue("@Id", user.Id);
            cmd.Parameters.AddWithValue("@DisplayName", user.DisplayName);
            cmd.Parameters.AddWithValue("@Role", (int)user.Role);
            cmd.Parameters.AddWithValue("@IsActive", user.IsActive ? 1 : 0);

            await cmd.ExecuteNonQueryAsync();
            return (true, "用户更新成功");
        }
        catch (Exception ex)
        {
            return (false, $"更新用户失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 修改密码
    /// </summary>
    public async Task<(bool Success, string Message)> ChangePasswordAsync(int userId, string oldPassword, string newPassword)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            // 获取用户
            using var getCmd = new SqliteCommand(
                "SELECT * FROM Users WHERE Id = @Id", connection);
            getCmd.Parameters.AddWithValue("@Id", userId);

            using var reader = await getCmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return (false, "用户不存在");
            }

            var user = MapUserFromReader(reader);

            // 验证旧密码
            if (!user.VerifyPassword(oldPassword))
            {
                return (false, "原密码错误");
            }

            // 更新密码
            user.SetPassword(newPassword);

            using var updateCmd = new SqliteCommand(
                "UPDATE Users SET PasswordHash = @PasswordHash WHERE Id = @Id", connection);
            updateCmd.Parameters.AddWithValue("@Id", userId);
            updateCmd.Parameters.AddWithValue("@PasswordHash", user.PasswordHash);

            await updateCmd.ExecuteNonQueryAsync();
            return (true, "密码修改成功");
        }
        catch (Exception ex)
        {
            return (false, $"修改密码失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 重置密码（管理员功能）
    /// </summary>
    public async Task<(bool Success, string Message)> ResetPasswordAsync(int userId, string newPassword)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var user = new User();
            user.SetPassword(newPassword);

            using var cmd = new SqliteCommand(
                "UPDATE Users SET PasswordHash = @PasswordHash WHERE Id = @Id", connection);
            cmd.Parameters.AddWithValue("@Id", userId);
            cmd.Parameters.AddWithValue("@PasswordHash", user.PasswordHash);

            await cmd.ExecuteNonQueryAsync();
            return (true, "密码重置成功");
        }
        catch (Exception ex)
        {
            return (false, $"重置密码失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 删除用户
    /// </summary>
    public async Task<(bool Success, string Message)> DeleteUserAsync(int userId)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            // 不能删除当前登录用户
            if (_currentUser?.Id == userId)
            {
                return (false, "不能删除当前登录的用户");
            }

            // 不能删除最后一个管理员
            using (var checkCmd = new SqliteCommand(@"
                SELECT COUNT(*) FROM Users 
                WHERE Role = @Role AND IsActive = 1 AND Id != @Id", connection))
            {
                checkCmd.Parameters.AddWithValue("@Role", (int)UserRole.Admin);
                checkCmd.Parameters.AddWithValue("@Id", userId);
                var adminCount = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());

                if (adminCount == 0)
                {
                    // 检查要删除的是否是管理员
                    using var userCmd = new SqliteCommand(
                        "SELECT Role FROM Users WHERE Id = @Id", connection);
                    userCmd.Parameters.AddWithValue("@Id", userId);
                    var role = Convert.ToInt32(await userCmd.ExecuteScalarAsync());

                    if (role == (int)UserRole.Admin)
                    {
                        return (false, "不能删除最后一个管理员账户");
                    }
                }
            }

            using var cmd = new SqliteCommand(
                "DELETE FROM Users WHERE Id = @Id", connection);
            cmd.Parameters.AddWithValue("@Id", userId);

            await cmd.ExecuteNonQueryAsync();
            return (true, "用户删除成功");
        }
        catch (Exception ex)
        {
            return (false, $"删除用户失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 获取登录记录
    /// </summary>
    public async Task<List<LoginRecord>> GetLoginRecordsAsync(int? userId = null, int limit = 100)
    {
        var records = new List<LoginRecord>();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var sql = "SELECT * FROM LoginRecords";
        if (userId.HasValue)
        {
            sql += " WHERE UserId = @UserId";
        }
        sql += " ORDER BY LoginTime DESC LIMIT @Limit";

        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Limit", limit);
        if (userId.HasValue)
        {
            cmd.Parameters.AddWithValue("@UserId", userId.Value);
        }

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            records.Add(new LoginRecord
            {
                Id = reader.GetInt32(0),
                UserId = reader.GetInt32(1),
                Username = reader.GetString(2),
                LoginTime = DateTime.Parse(reader.GetString(3)),
                LogoutTime = reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4)),
                LoginIp = reader.IsDBNull(5) ? "" : reader.GetString(5),
                IsSuccess = reader.GetInt32(6) == 1,
                FailReason = reader.IsDBNull(7) ? "" : reader.GetString(7)
            });
        }

        return records;
    }

    #region 私有方法

    private void InsertUserInternal(SqliteConnection connection, User user)
    {
        using var cmd = new SqliteCommand(@"
            INSERT INTO Users (Username, DisplayName, PasswordHash, Role, IsActive, CreatedAt)
            VALUES (@Username, @DisplayName, @PasswordHash, @Role, @IsActive, @CreatedAt)
            RETURNING Id", connection);

        cmd.Parameters.AddWithValue("@Username", user.Username);
        cmd.Parameters.AddWithValue("@DisplayName", user.DisplayName);
        cmd.Parameters.AddWithValue("@PasswordHash", user.PasswordHash);
        cmd.Parameters.AddWithValue("@Role", (int)user.Role);
        cmd.Parameters.AddWithValue("@IsActive", user.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("@CreatedAt", user.CreatedAt.ToString("O"));

        user.Id = Convert.ToInt32(cmd.ExecuteScalar()!);
    }

    private async Task UpdateLastLoginAsync(SqliteConnection connection, User user)
    {
        using var cmd = new SqliteCommand(@"
            UPDATE Users SET 
                LastLoginAt = @LastLoginAt,
                LastLoginIp = @LastLoginIp
            WHERE Id = @Id", connection);

        cmd.Parameters.AddWithValue("@Id", user.Id);
        cmd.Parameters.AddWithValue("@LastLoginAt", user.LastLoginAt.ToString("O"));
        cmd.Parameters.AddWithValue("@LastLoginIp", user.LastLoginIp ?? (object)DBNull.Value);

        await cmd.ExecuteNonQueryAsync();
    }

    private void UpdateLastLoginSync(SqliteConnection connection, User user)
    {
        using var cmd = new SqliteCommand(@"
            UPDATE Users SET 
                LastLoginAt = @LastLoginAt,
                LastLoginIp = @LastLoginIp
            WHERE Id = @Id", connection);

        cmd.Parameters.AddWithValue("@Id", user.Id);
        cmd.Parameters.AddWithValue("@LastLoginAt", user.LastLoginAt.ToString("O"));
        cmd.Parameters.AddWithValue("@LastLoginIp", user.LastLoginIp ?? (object)DBNull.Value);

        cmd.ExecuteNonQuery();
    }

    private async Task RecordLoginAsync(int userId, string username, bool isSuccess, string ipAddress, string failReason)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var cmd = new SqliteCommand(@"
                INSERT INTO LoginRecords (UserId, Username, LoginTime, LoginIp, IsSuccess, FailReason)
                VALUES (@UserId, @Username, @LoginTime, @LoginIp, @IsSuccess, @FailReason)", connection);

            cmd.Parameters.AddWithValue("@UserId", userId);
            cmd.Parameters.AddWithValue("@Username", username);
            cmd.Parameters.AddWithValue("@LoginTime", DateTime.Now.ToString("O"));
            cmd.Parameters.AddWithValue("@LoginIp", ipAddress);
            cmd.Parameters.AddWithValue("@IsSuccess", isSuccess ? 1 : 0);
            cmd.Parameters.AddWithValue("@FailReason", failReason ?? (object)DBNull.Value);

            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // 记录日志失败不应影响登录流程
        }
    }

    private void RecordLoginSync(int userId, string username, bool isSuccess, string ipAddress, string failReason)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var cmd = new SqliteCommand(@"
                INSERT INTO LoginRecords (UserId, Username, LoginTime, LoginIp, IsSuccess, FailReason)
                VALUES (@UserId, @Username, @LoginTime, @LoginIp, @IsSuccess, @FailReason)", connection);

            cmd.Parameters.AddWithValue("@UserId", userId);
            cmd.Parameters.AddWithValue("@Username", username);
            cmd.Parameters.AddWithValue("@LoginTime", DateTime.Now.ToString("O"));
            cmd.Parameters.AddWithValue("@LoginIp", ipAddress);
            cmd.Parameters.AddWithValue("@IsSuccess", isSuccess ? 1 : 0);
            cmd.Parameters.AddWithValue("@FailReason", failReason ?? (object)DBNull.Value);

            cmd.ExecuteNonQuery();
        }
        catch
        {
            // 记录日志失败不应影响登录流程
        }
    }

    private void UpdateLogoutTime(int userId)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var cmd = new SqliteCommand(@"
                UPDATE LoginRecords 
                SET LogoutTime = @LogoutTime
                WHERE UserId = @UserId AND LogoutTime IS NULL
                ORDER BY LoginTime DESC
                LIMIT 1", connection);

            cmd.Parameters.AddWithValue("@UserId", userId);
            cmd.Parameters.AddWithValue("@LogoutTime", DateTime.Now.ToString("O"));

            cmd.ExecuteNonQuery();
        }
        catch
        {
            // 忽略错误
        }
    }

    private User MapUserFromReader(SqliteDataReader reader)
    {
        return new User
        {
            Id = reader.GetInt32(0),
            Username = reader.GetString(1),
            DisplayName = reader.GetString(2),
            PasswordHash = reader.GetString(3),
            Role = (UserRole)reader.GetInt32(4),
            IsActive = reader.GetInt32(5) == 1,
            CreatedAt = DateTime.Parse(reader.GetString(6)),
            LastLoginAt = reader.IsDBNull(7) ? DateTime.MinValue : DateTime.Parse(reader.GetString(7)),
            LastLoginIp = reader.IsDBNull(8) ? "" : reader.GetString(8)
        };
    }

    #endregion
}
