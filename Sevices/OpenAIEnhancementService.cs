using System.Text;
using System.Text.Json;

namespace VoiceTranslateMvp.Services
{
    /// <summary>
    /// OPTIONAL: ChatGPT-powered intelligence for better story translation
    /// Provides context-aware translation with narrative understanding
    /// </summary>
    public class OpenAIEnhancementService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly Queue<string> _conversationHistory = new();
        private const int MAX_HISTORY = 5;

        public OpenAIEnhancementService(IConfiguration config)
        {
            _httpClient = new HttpClient();
            _apiKey = config["OpenAI:ApiKey"] ?? throw new Exception("OpenAI:ApiKey not configured");
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
        }

        /// <summary>
        /// Enhance translation with ChatGPT intelligence
        /// Understands story context, maintains character names, narrative flow
        /// </summary>
        public async Task<string> EnhanceTranslationAsync(
            string englishSentence,
            List<string> contextSentences,
            string rawArabicTranslation)
        {
            try
            {
                var context = string.Join(" ", contextSentences);

                var systemPrompt = @"You are a professional translator specializing in English to Arabic translation for children's stories.

Your task:
1. Review the provided Arabic translation
2. Ensure it maintains narrative consistency with previous context
3. Keep character names consistent
4. Preserve story tone and emotion
5. Make it natural and engaging for children

Return ONLY the improved Arabic translation of the current sentence, nothing else.";

                var userPrompt = $@"Context (previous sentences in English):
{(string.IsNullOrEmpty(context) ? "None - this is the first sentence" : context)}

Current English sentence to translate:
{englishSentence}

Raw machine translation (Arabic):
{rawArabicTranslation}

Please provide an improved, context-aware Arabic translation of ONLY the current sentence:";

                var messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                };

                var requestBody = new
                {
                    model = "gpt-4o-mini", // Fast and cost-effective
                    messages = messages,
                    temperature = 0.3, // Low temperature for consistency
                    max_tokens = 200
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync("https://api.openai.com/v1/chat/completions", content);
                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<ChatGPTResponse>(responseJson);

                var enhancedTranslation = result?.choices?[0]?.message?.content?.Trim() ?? rawArabicTranslation;

                Console.WriteLine($"🤖 ChatGPT enhanced: '{rawArabicTranslation}' → '{enhancedTranslation}'");

                return enhancedTranslation;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ ChatGPT enhancement failed, using raw translation: {ex.Message}");
                return rawArabicTranslation; // Fallback to original
            }
        }

        /// <summary>
        /// Get story guidance: suggest what might come next, fix narrative issues
        /// </summary>
        public async Task<string> GetStoryGuidanceAsync(List<string> storySoFar)
        {
            try
            {
                var story = string.Join(" ", storySoFar);

                var prompt = $@"You are helping translate a children's story from English to Arabic.

Story so far:
{story}

Provide brief guidance:
1. Are there any narrative inconsistencies?
2. Any character names that should be standardized?
3. Suggested improvements for better flow?

Keep response under 100 words.";

                var requestBody = new
                {
                    model = "gpt-4o-mini",
                    messages = new[]
                    {
                        new { role = "user", content = prompt }
                    },
                    temperature = 0.5,
                    max_tokens = 150
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync("https://api.openai.com/v1/chat/completions", content);
                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<ChatGPTResponse>(responseJson);

                return result?.choices?[0]?.message?.content?.Trim() ?? "No guidance available";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Story guidance failed: {ex.Message}");
                return string.Empty;
            }
        }

        // Response DTOs
        private class ChatGPTResponse
        {
            public Choice[]? choices { get; set; }
        }

        private class Choice
        {
            public Message? message { get; set; }
        }

        private class Message
        {
            public string? content { get; set; }
        }
    }
}