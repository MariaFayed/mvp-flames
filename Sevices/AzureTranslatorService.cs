using Newtonsoft.Json.Linq;
using System.Net.Http.Headers;
using System.Text;

namespace VoiceTranslateMvp.Services;

public class AzureTranslatorService
{
    private readonly string _key;
    private readonly string _endpoint;
    private readonly string _region;

    public AzureTranslatorService(IConfiguration configuration)
    {
        _key = configuration["AzureTranslator:Key"]
               ?? throw new Exception("Missing AzureTranslator:Key in configuration");
        _endpoint = configuration["AzureTranslator:Endpoint"]
                    ?? throw new Exception("Missing AzureTranslator:Endpoint in configuration");
        _region = configuration["AzureTranslator:Region"]
                  ?? throw new Exception("Missing AzureTranslator:Region in configuration");
    }

    /// <summary>
    /// Translate English text to Arabic using Azure Translator.
    /// </summary>
    public async Task<string> TranslateEnToArAsync(string text)
    {
        var url = _endpoint.TrimEnd('/') + "/translate?api-version=3.0&from=en&to=ar";

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", _key);
        client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Region", _region);

        var body = new object[] { new { Text = text } };
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(body);

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(url, content);
        var responseText = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Translator error: {response.StatusCode} - {responseText}");
        }

        var jArray = JArray.Parse(responseText);
        var arabic = jArray[0]["translations"]?[0]?["text"]?.ToString();

        return arabic ?? string.Empty;
    }
}
