using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace VoiceTranslateMvp.Services;

/// <summary>
/// ✅ STEP 3: Multi-language translator using OpenAI GPT-4
/// Supports: Arabic (MSA), French, German, Spanish, Bangla, Mandarin
/// Context-aware translation optimized for children's stories
/// </summary>
public class OpenAITranslatorService
{
    private readonly string _apiKey;
    private readonly HttpClient _http;
    private const string API_URL = "https://api.openai.com/v1/chat/completions";

    // Language display names for prompting
    private static readonly Dictionary<string, string> LanguageNames = new()
    {
        { "ar", "Modern Standard Arabic (MSA/فصحى)" },
        { "fr", "French" },
        { "de", "German" },
        { "es", "Spanish" },
        { "bn", "Bangla (Bangladesh)" },
        { "zh", "Mandarin Chinese (Simplified)" }
    };

    // Base system prompt - will be customized per language
    private const string SYSTEM_PROMPT_TEMPLATE = @"You are an expert English-to-{LANGUAGE} translator specializing in children's stories.

# Critical Rules:
1. Translate ONLY the current sentence - DO NOT include context in output
2. DO NOT add explanations, notes, or commentary
3. Use natural, child-friendly language - avoid overly formal or academic tone
4. Keep it simple and engaging for children (like a bedtime story)
5. Maintain consistency with previous context (character names, pronouns, etc.)
6. Output ONLY the {LANGUAGE} translation, nothing else

# Translation Style:
- Natural and engaging for children
- Simple sentence structure
- Warm, friendly tone
- Consistent character names across the story

Remember: Use the context to maintain consistency, but translate ONLY the current sentence.";

    public OpenAITranslatorService(IConfiguration configuration)
    {
        _apiKey = configuration["OpenAI:ApiKey"]
                  ?? throw new Exception("Missing OpenAI:ApiKey in configuration");

        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    /// <summary>
    /// ✅ Main translation method - supports all languages
    /// AGGRESSIVE MODE: Minimal context for fastest response
    /// </summary>
    public async Task<string> TranslateAsync(
        string englishText,
        string targetLanguage,
        List<string> previousSentences = null)
    {
        if (string.IsNullOrWhiteSpace(englishText))
            return string.Empty;

        // Validate language
        if (!LanguageNames.TryGetValue(targetLanguage, out var languageName))
        {
            throw new Exception($"Unsupported language: {targetLanguage}. Supported: {string.Join(", ", LanguageNames.Keys)}");
        }

        try
        {
            // Build system prompt for target language
            var systemPrompt = SYSTEM_PROMPT_TEMPLATE.Replace("{LANGUAGE}", languageName);

            // ✅ AGGRESSIVE: Very minimal context for speed
            string userPrompt;

            if (previousSentences != null && previousSentences.Count > 0)
            {
                // Only use the very last sentence for minimal context
                var lastSentence = previousSentences.LastOrDefault();
                userPrompt = $@"Previous: {lastSentence}
Current: {englishText}
Translate to {languageName}:";
            }
            else
            {
                userPrompt = $@"{englishText} → {languageName}:";
            }

            var payload = new
            {
                model = "gpt-4o-mini",
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                temperature = 0.3,
                max_tokens = 100 // Reduced from 150 for speed
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _http.PostAsync(API_URL, content);
            var responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[Translator] OpenAI API error: {response.StatusCode}");
                Console.WriteLine($"[Translator] Response: {responseText}");
                throw new Exception($"OpenAI API error: {response.StatusCode} - {responseText}");
            }

            using var doc = JsonDocument.Parse(responseText);

            var choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() > 0)
            {
                var message = choices[0].GetProperty("message");
                var translatedText = message.GetProperty("content").GetString();

                if (!string.IsNullOrWhiteSpace(translatedText))
                {
                    // Clean any formatting ChatGPT might add
                    translatedText = CleanTranslation(translatedText);

                    Console.WriteLine($"[Translator {targetLanguage.ToUpper()}] EN: {englishText}");
                    Console.WriteLine($"[Translator {targetLanguage.ToUpper()}] {targetLanguage.ToUpper()}: {translatedText}");

                    return translatedText;
                }
            }

            throw new Exception("Unexpected response format from OpenAI API");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Translator {targetLanguage.ToUpper()}] Error: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Clean any markdown or formatting ChatGPT might add
    /// </summary>
    private string CleanTranslation(string text)
    {
        // Remove markdown formatting
        text = text.Replace("**", "").Replace("*", "");

        // Remove common prefixes ChatGPT might add
        var prefixPatterns = new[]
        {
            @"^(Translation:|Arabic:|French:|German:|Spanish:|Bangla:|Mandarin:|MSA:|الترجمة:|العربية:)\s*",
            @"^(Here is the translation:|The translation is:)\s*",
            @"^\s*[""«»]\s*",  // Remove leading quotes
            @"\s*[""«»]\s*$"   // Remove trailing quotes
        };

        foreach (var pattern in prefixPatterns)
        {
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                pattern,
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        return text.Trim();
    }

    /// <summary>
    /// Translate multiple sentences in parallel for better performance
    /// </summary>
    public async Task<List<string>> TranslateBatchAsync(List<string> englishTexts, string targetLanguage)
    {
        var tasks = englishTexts.Select(text => TranslateAsync(text, targetLanguage));
        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    // ✅ LEGACY: Keep old method name for backward compatibility
    public async Task<string> TranslateEnToMSAAsync(string currentSentence, List<string> previousSentences = null)
    {
        return await TranslateAsync(currentSentence, "ar", previousSentences);
    }
}