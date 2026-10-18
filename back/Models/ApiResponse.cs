namespace ERental.Models;

public class ApiResponse<T>
{
    public int Code { get; init; } = 200;
    public string Message { get; init; } = "success";
    public bool Success { get; init; } = true;
    public T? Data { get; init; }
}
