using Microsoft.AspNetCore.Mvc;
using System.Net.Http;

namespace MalevaAPI.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class WeatherForecastController : ControllerBase
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private static readonly string[] Summaries =
        [
            "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
        ];
        public WeatherForecastController(IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
        }
        [HttpGet(Name = "GetWeatherForecast")]
        public IEnumerable<WeatherForecast> Get()
        {
            return Enumerable.Range(1, 5).Select(index => new WeatherForecast
            {
                Date = DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
                TemperatureC = Random.Shared.Next(-20, 55),
                Summary = Summaries[Random.Shared.Next(Summaries.Length)]
            })
            .ToArray();
        }

        // Disambiguated route: GET /WeatherForecast/sample
        [HttpGet("sample", Name = "GetWeatherForecastSample")]
        public IEnumerable<WeatherForecast> GetWeatherForecastSample()
        {
            return Enumerable.Range(1, 5).Select(index => new WeatherForecast
            {
                Date = DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
                TemperatureC = Random.Shared.Next(-20, 55),
                Summary = Summaries[Random.Shared.Next(Summaries.Length)]
            })
            .ToArray();
        }


        // New endpoint: GET /WeatherForecast/report
        // - If query parameter "reportUrl" is provided it will be used.
        // - Otherwise the code will attempt to read "CrystalReport:Endpoint" from configuration.
        // The action fetches the remote content and returns it unchanged (preserves content-type and filename when available).
        [HttpGet("report", Name = "GetCrystalReport")]
        public async Task<IActionResult> GetCrystalReport([FromQuery] string? reportUrl)
        {
            //http://localhost:5127/WeatherForecast/report?reportUrl=https://europe1.discourse-cdn.com/flex013/uploads/make/original/3X/8/c/8ceb6d44d838fa16e2cb9e85c0942cd5527f23d1.jpeg
            var url = reportUrl ?? _configuration["CrystalReport:Endpoint"];
            if (string.IsNullOrWhiteSpace(url))
            {
                return BadRequest("Report URL not specified and configuration key 'CrystalReport:Endpoint' is not set.");
            }

            var client = _httpClientFactory.CreateClient();
            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(url);
            }
            catch (HttpRequestException ex)
            {
                return StatusCode(502, $"Failed to fetch report: {ex.Message}");
            }

            if (!response.IsSuccessStatusCode)
            {
                return StatusCode((int)response.StatusCode, $"Upstream returned {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var contentBytes = await response.Content.ReadAsByteArrayAsync();
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";

            // Try to obtain filename from Content-Disposition header if provided
            string? fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                               ?? response.Content.Headers.ContentDisposition?.FileName;
            if (!string.IsNullOrEmpty(fileName))
            {
                fileName = fileName.Trim('"');
            }
            else
            {
                // fallback extension hints
                if (string.Equals(mediaType, "application/pdf", StringComparison.OrdinalIgnoreCase))
                {
                    fileName = "report.pdf";
                }
                else
                {
                    fileName = "report";
                }
            }

            return File(contentBytes, mediaType, fileName);
        }
    }
}
