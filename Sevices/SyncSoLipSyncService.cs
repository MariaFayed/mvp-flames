using System.Net.Http.Headers;
using System.Text.Json;

namespace VoiceTranslateMvp.Services
{
    public class SyncSoLipSyncService
    {
        private readonly HttpClient _http;
        private readonly string _apiKey;

        public SyncSoLipSyncService(IConfiguration config)
        {
            _apiKey = config["SyncSo:ApiKey"] ?? throw new Exception("Missing SyncSo:ApiKey");
            _http = new HttpClient();
            _http.DefaultRequestHeaders.Add("x-api-key", _apiKey); // Sync uses x-api-key 
        }

        public async Task<string> CreateGenerationWithFilesAsync(string videoPath, string audioPath, string model)
        {
            using var form = new MultipartFormDataContent();

            // field names: "video" and "audio" 
            var videoContent = new StreamContent(File.OpenRead(videoPath));
            videoContent.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            form.Add(videoContent, "video", Path.GetFileName(videoPath));

            var audioContent = new StreamContent(File.OpenRead(audioPath));
            audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(audioContent, "audio", Path.GetFileName(audioPath));

            form.Add(new StringContent(model), "model");

            var resp = await _http.PostAsync("https://api.sync.so/v2/generate", form);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Sync create failed: {(int)resp.StatusCode} - {body}");

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("id").GetString()!;
        }

        public async Task<string> WaitForOutputUrlAsync(string jobId, TimeSpan timeout, TimeSpan pollEvery)
        {
            var started = DateTime.UtcNow;

            while (DateTime.UtcNow - started < timeout)
            {
                var resp = await _http.GetAsync($"https://api.sync.so/v2/generate/{jobId}");
                var body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                    throw new Exception($"Sync get failed: {(int)resp.StatusCode} - {body}");

                using var doc = JsonDocument.Parse(body);
                var status = doc.RootElement.GetProperty("status").GetString();
                var outputUrl = doc.RootElement.TryGetProperty("outputUrl", out var ou) ? ou.GetString() : null;
                var error = doc.RootElement.TryGetProperty("error", out var er) ? er.GetString() : null;

                if (status == "COMPLETED" && !string.IsNullOrWhiteSpace(outputUrl))
                    return outputUrl!;

                if (status == "FAILED" || status == "REJECTED")
                    throw new Exception($"Sync job {status}: {error}");

                await Task.Delay(pollEvery);
            }

            throw new Exception("Sync job timed out.");
        }

        public async Task<byte[]> DownloadOutputAsync(string outputUrl)
        {
            // outputUrl is a normal URL to the generated video 
            return await _http.GetByteArrayAsync(outputUrl);
        }
    }
}
