using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace VoiceTranslateMvp.Services;

/// <summary>
/// Translates English to Egyptian Arabic dialect (Amya) using OpenAI GPT-4
/// Uses a two-step approach: English → MSA → Egyptian dialect for best quality
/// </summary>
public class EgyptianDialectTranslatorService
{
    private readonly string _apiKey;
    private readonly HttpClient _http;
    private const string API_URL = "https://api.openai.com/v1/chat/completions";

    // System prompt optimized for direct Egyptian dialect translation
    private const string SYSTEM_PROMPT = @"You are an expert translator specializing in Egyptian Arabic dialect (Amya/عامية مصرية).
Translate directly from English to Egyptian dialect as naturally spoken in Cairo.

# Critical Rules:
1. Output ONLY the Egyptian Arabic translation - no explanations, no notes, no English.
2. Use natural, conversational Egyptian dialect (عامية مصرية) - NOT formal Arabic (فصحى).
3. Use everyday Egyptian expressions, slang, and colloquialisms.
4. Match the tone: casual English = casual Egyptian, formal English = polite Egyptian.
5. For common words, use Egyptian pronunciation (e.g., 'ازيك' not 'كيف حالك').
6. Keep it short and natural - Egyptians speak casually, not in long sentences.

Examples:
- 'Hello, how are you?' → 'أهلا، ازيك؟'
- 'What's up?' → 'ايه الأخبار؟'
- 'I'm fine, thanks' → 'أنا كويس، شكراً'
- 'See you later' → 'أشوفك بعدين'

Remember: Direct translation to Egyptian dialect only. No extra text.";

    public EgyptianDialectTranslatorService(IConfiguration configuration)
    {
        _apiKey = configuration["OpenAI:ApiKey"]
                  ?? throw new Exception("Missing OpenAI:ApiKey in configuration");

        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    /// <summary>
    /// Translate English text to Egyptian dialect (Amya)
    /// </summary>
    public async Task<string> TranslateEnToEgyptianAsync(string englishText)
    {
        if (string.IsNullOrWhiteSpace(englishText))
            return string.Empty;

        var payload = new
        {
            model = "gpt-4o-mini", // Using mini for faster, cheaper translations
            messages = new[]
            {
                new { role = "system", content = SYSTEM_PROMPT },
                new { role = "user", content = englishText }
            },
            temperature = 0.3, // Lower temp for more consistent, direct translations
            max_tokens = 500 // Reduced since we're doing direct translation
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var response = await _http.PostAsync(API_URL, content);
            var responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[Egyptian Translator] OpenAI API error: {response.StatusCode}");
                Console.WriteLine($"[Egyptian Translator] Response: {responseText}");
                throw new Exception($"OpenAI API error: {response.StatusCode} - {responseText}");
            }

            using var doc = JsonDocument.Parse(responseText);

            // Parse OpenAI response format
            var choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() > 0)
            {
                var message = choices[0].GetProperty("message");
                var translatedText = message.GetProperty("content").GetString();

                if (!string.IsNullOrWhiteSpace(translatedText))
                {
                    Console.WriteLine($"[Egyptian Translator] EN: {englishText}");
                    Console.WriteLine($"[Egyptian Translator] EG: {translatedText}");
                    return translatedText.Trim();
                }
            }

            throw new Exception("Unexpected response format from OpenAI API");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Egyptian Translator] Error: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Translate multiple sentences in parallel for better performance
    /// </summary>
    public async Task<List<string>> TranslateBatchAsync(List<string> englishTexts)
    {
        var tasks = englishTexts.Select(text => TranslateEnToEgyptianAsync(text));
        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }
}