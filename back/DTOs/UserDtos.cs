namespace ERental.DTOs;

public class CreateUserDto
{
    public string LoginAccount { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string UserName { get; init; } = string.Empty;
    public string RoleType { get; init; } = string.Empty; // reception / finance
}

public class UpdateUserDto
{
    public string? UserName { get; init; }
    public string? Password { get; init; }
    public int? UserStatus { get; init; }
}

public class UserDto
{
    public int UserId { get; init; }
    public string LoginAccount { get; init; } = string.Empty;
    public string UserName { get; init; } = string.Empty;
    public string RoleName { get; init; } = string.Empty;
    public int UserStatus { get; init; }
    public int LoginFailCount { get; init; }
}

