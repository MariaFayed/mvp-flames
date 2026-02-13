using System.Net.Http.Headers;
using System.Text.Json;

namespace VoiceTranslateMvp.Services
{
    public class OpenAITranscriptionService
    {
        private readonly HttpClient _http;
        private readonly string _apiKey;
        private readonly string _model;

        public OpenAITranscriptionService(IConfiguration config, HttpClient http)
        {
            _http = http;
            _apiKey = config["OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing OpenAI:ApiKey");
            _model = config["OpenAI:TranscribeModel"] ?? "gpt-4o-mini-transcribe";
        }

        // ✅ NEW: Transcribe from in-memory WAV bytes (no temp files)
        public async Task<string> TranscribeEnglishFromWavBytesAsync(byte[] wavBytes, string language = "en")
        {
            using var form = new MultipartFormDataContent();

            form.Add(new StringContent(_model), "model");

            // Optional but helps reduce weird guesses
            form.Add(new StringContent(language), "language");

            // Optional: set temperature=0 if supported for your model endpoint
            // form.Add(new StringContent("0"), "temperature");

            var fileContent = new ByteArrayContent(wavBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

            // Name MUST be "file"
            form.Add(fileContent, "file", "audio.wav");

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/transcriptions");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            req.Content = form;

            using var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync();

            // Response format usually: { "text": "..." }
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";
        }
    }
}
