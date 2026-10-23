using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

public class APSException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public APSException(HttpStatusCode statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}

// Minimal helper for the APS endpoints called directly over HTTP
// (Design Automation, OSS signed URLs, Data Management writes, Model Derivative, Data Exchange GraphQL)
public partial class APS
{
    private const string BaseUrl = "https://developer.api.autodesk.com";
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(5) };

    private static async Task<JsonNode> SendAsync(HttpMethod method, string url, string accessToken, JsonNode body = null,
        string contentType = "application/json", IDictionary<string, string> headers = null)
    {
        using var request = new HttpRequestMessage(method, url.StartsWith("https://") ? url : BaseUrl + url);
        if (accessToken != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
        if (headers != null)
        {
            foreach (var header in headers)
            {
                request.Headers.Add(header.Key, header.Value);
            }
        }
        if (body != null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }
        using var response = await _httpClient.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new APSException(response.StatusCode, $"{method} {request.RequestUri} failed ({(int)response.StatusCode}): {text}");
        }
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    private static Task<JsonNode> GetAsync(string url, string accessToken, IDictionary<string, string> headers = null)
        => SendAsync(HttpMethod.Get, url, accessToken, headers: headers);

    private static Task<JsonNode> PostAsync(string url, string accessToken, JsonNode body, string contentType = "application/json", IDictionary<string, string> headers = null)
        => SendAsync(HttpMethod.Post, url, accessToken, body, contentType, headers);

    private static string Encode(string value) => Uri.EscapeDataString(value);

    // URL-safe base64 without padding, as used for Model Derivative URNs
    public static string ToUrlSafeBase64(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
