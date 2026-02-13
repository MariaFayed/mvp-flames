using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace VoiceTranslateMvp.Services;

/// <summary>
/// Context-aware MSA (Modern Standard Arabic) translator using OpenAI GPT-4
/// Optimized for children's stories with natural, engaging language
/// Compatible with Azure Speech TTS (ar-EG-SalmaNeural)
/// </summary>
public class OpenAIMSATranslatorService
{
    private readonly string _apiKey;
    private readonly HttpClient _http;
    private const string API_URL = "https://api.openai.com/v1/chat/completions";

    // System prompt optimized for MSA translation with context awareness
    private const string SYSTEM_PROMPT = @"You are an expert English-to-Arabic translator specializing in Modern Standard Arabic (MSA/فصحى) for children's stories.

# Critical Rules:
1. Translate ONLY the current sentence - DO NOT include context in output
2. DO NOT add explanations, notes, or commentary
3. Use Modern Standard Arabic (فصحى) - NOT dialect (عامية)
4. Keep it natural and child-friendly - avoid overly formal classical Arabic
5. Use simple, clear vocabulary suitable for children
6. Maintain consistency with previous context (character names, pronouns, etc.)
7. Output ONLY the Arabic translation, nothing else

# Translation Style:
- Natural and engaging for children (like a bedtime story)
- Simple sentence structure
- Warm, friendly tone
- Consistent character names across the story

# Examples:
English: ""Once upon a time.""
Arabic: ""كان يا ما كان.""

English: ""There was a princess.""
Arabic: ""كانت هناك أميرة.""

English: ""She lived in a beautiful castle.""
Arabic: ""كانت تعيش في قصر جميل.""

Remember: Use the context to maintain consistency, but translate ONLY the current sentence.";

    public OpenAIMSATranslatorService(IConfiguration configuration)
    {
        _apiKey = configuration["OpenAI:ApiKey"]
                  ?? throw new Exception("Missing OpenAI:ApiKey in configuration");

        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    /// <summary>
    /// Translate English text to MSA Arabic with context awareness
    /// Returns ONLY the translation of the current sentence
    /// </summary>
    public async Task<string> TranslateEnToMSAAsync(string currentSentence, List<string> previousSentences = null)
    {
        if (string.IsNullOrWhiteSpace(currentSentence))
            return string.Empty;

        try
        {
            // Build user prompt with context
            string userPrompt;

            if (previousSentences != null && previousSentences.Count > 0)
            {
                var context = string.Join(" ", previousSentences);
                userPrompt = $@"Previous story context (for consistency only):
{context}

Current sentence to translate:
{currentSentence}

Translate to MSA Arabic:";
            }
            else
            {
                userPrompt = $@"Translate this sentence to MSA Arabic:
{currentSentence}

MSA Arabic translation:";
            }

            var payload = new
            {
                model = "gpt-4o-mini", // Fast, cheap, excellent quality
                messages = new[]
                {
                    new { role = "system", content = SYSTEM_PROMPT },
                    new { role = "user", content = userPrompt }
                },
                temperature = 0.3, // Low for consistency
                max_tokens = 200
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _http.PostAsync(API_URL, content);
            var responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[MSA Translator] OpenAI API error: {response.StatusCode}");
                Console.WriteLine($"[MSA Translator] Response: {responseText}");
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

                    Console.WriteLine($"[MSA Translator] EN: {currentSentence}");
                    Console.WriteLine($"[MSA Translator] AR: {translatedText}");

                    return translatedText;
                }
            }

            throw new Exception("Unexpected response format from OpenAI API");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MSA Translator] Error: {ex.Message}");
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
            @"^(Translation:|Arabic:|MSA:|الترجمة:|العربية:)\s*",
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
    /// Useful for batch processing
    /// </summary>
    public async Task<List<string>> TranslateBatchAsync(List<string> englishTexts)
    {
        var tasks = englishTexts.Select(text => TranslateEnToMSAAsync(text));
        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }
}