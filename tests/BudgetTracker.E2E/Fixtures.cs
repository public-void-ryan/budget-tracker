using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;

namespace BudgetTracker.E2E;

/// <summary>Data set up through the API, so a UI test starts from the state it is about, not from a long click path.</summary>
public sealed class Api(AppFixture app, string token)
{
    private readonly HttpClient _http = Create(app, token);

    private static HttpClient Create(AppFixture app, string token)
    {
        var http = new HttpClient { BaseAddress = new Uri(app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return http;
    }

    public static async Task<Api> SignedInAsync(AppFixture app, IPage page)
    {
        var token = await page.EvaluateAsync<string>("sessionStorage.getItem('bt.token')");
        return new Api(app, token);
    }

    public async Task<Guid> CategoryAsync(string name) => await CreateAsync("/categories", new { name });

    public async Task<Guid> AccountAsync(string name, string type = "Checking") =>
        await CreateAsync("/accounts", new { name, type });

    public Task<Guid> TransactionAsync(Guid account, string date, decimal amount, string description, Guid? category = null) =>
        CreateAsync("/transactions", new { accountId = account, categoryId = category, postedOn = date, amount, description });

    public async Task<JsonElement> GetAsync(string path) =>
        await _http.GetFromJsonAsync<JsonElement>(path);

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _http.SendAsync(request);
    }

    private async Task<Guid> CreateAsync(string path, object body)
    {
        var response = await _http.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}
