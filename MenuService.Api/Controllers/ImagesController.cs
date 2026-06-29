using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MenuService.Application.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MenuService.Api.Controllers;

[ApiController]
[Route("api/v1/images")]
[Authorize(Roles = "Admin")]
public class ImagesController : ControllerBase
{
    private const long MaxFileSize = 5 * 1024 * 1024;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;

    public ImagesController(IConfiguration configuration, IHttpClientFactory httpClientFactory)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
    }

    [HttpPost]
    [RequestSizeLimit(MaxFileSize)]
    public async Task<IActionResult> Upload([FromForm] IFormFile file)
    {
        if (file is null || file.Length == 0)
            return Error(StatusCodes.Status400BadRequest, "Debe seleccionar una imagen.");

        if (file.Length > MaxFileSize)
            return Error(StatusCodes.Status400BadRequest, "La imagen no puede superar los 5 MB.");

        if (string.IsNullOrWhiteSpace(file.ContentType) || !file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return Error(StatusCodes.Status400BadRequest, "El archivo debe ser una imagen.");

        var cloudName = _configuration["Cloudinary:CloudName"];
        var apiKey = _configuration["Cloudinary:ApiKey"];
        var apiSecret = _configuration["Cloudinary:ApiSecret"];

        if (string.IsNullOrWhiteSpace(cloudName) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
            return Error(StatusCodes.Status500InternalServerError, "Falta configurar Cloudinary.");

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var folder = _configuration["Cloudinary:Folder"] ?? "fastrestaurant";
        var signature = BuildSignature(folder, timestamp, apiSecret);

        using var content = new MultipartFormDataContent
        {
            { new StringContent(apiKey), "api_key" },
            { new StringContent(timestamp), "timestamp" },
            { new StringContent(folder), "folder" },
            { new StringContent(signature), "signature" }
        };

        await using var stream = file.OpenReadStream();
        using var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType);
        content.Add(fileContent, "file", file.FileName);

        var client = _httpClientFactory.CreateClient();
        var response = await client.PostAsync($"https://api.cloudinary.com/v1_1/{cloudName}/image/upload", content);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            return Error((int)response.StatusCode, "Cloudinary rechazó la imagen.");

        using var json = JsonDocument.Parse(responseBody);
        var url = json.RootElement.GetProperty("secure_url").GetString();

        return Ok(new { url });
    }

    private static string BuildSignature(string folder, string timestamp, string apiSecret)
    {
        var value = $"folder={folder}&timestamp={timestamp}{apiSecret}";
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static ObjectResult Error(int statusCode, string message)
    {
        return new ObjectResult(new ErrorResponseDto
        {
            Message = message,
            StatusCode = statusCode,
            Timestamp = DateTime.UtcNow
        })
        {
            StatusCode = statusCode
        };
    }
}
