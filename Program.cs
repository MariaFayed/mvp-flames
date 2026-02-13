using Microsoft.AspNetCore.Http.Features;
using System.Net.WebSockets;
using System.Text;
using VoiceTranslateMvp.DTO;
using VoiceTranslateMvp.Services;

Console.OutputEncoding = Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);

// Allow larger uploads if needed (up to 100 MB here)
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 1024L * 1024L * 100L; // 100 MB
});

// ═══════════════════════════════════════════════════════════
// ✅ STEP 6: SERVICE REGISTRATION - Multi-Language Support
// ═══════════════════════════════════════════════════════════

// ✅ Azure Speech (REQUIRED - for TTS + Visemes in ALL languages)
builder.Services.AddSingleton<AzureSpeechService>();

// ✅ OpenAI Transcription (Whisper - better than Azure STT)
builder.Services.AddHttpClient<OpenAITranscriptionService>();

// ✅ OpenAI Multi-Language Translator (ar, fr, de, es, bn, zh)
builder.Services.AddSingleton<OpenAITranslatorService>();

// ℹ️ OPTIONAL: Keep old services for backward compatibility
// builder.Services.AddSingleton<OpenAIMSATranslatorService>();
// builder.Services.AddSingleton<AzureTranslatorService>();
// builder.Services.AddSingleton<EgyptianDialectTranslatorService>();

// Optional: Lip sync service
builder.Services.AddSingleton<SyncSoLipSyncService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Verify services on startup
using (var scope = app.Services.CreateScope())
{
    Console.WriteLine("\n🔍 Service Configuration:");
    Console.WriteLine("════════════════════════════════════════");

    var azureSpeech = scope.ServiceProvider.GetService<AzureSpeechService>();
    Console.WriteLine(azureSpeech != null
        ? "✅ Azure Speech (Multi-language TTS + Visemes)"
        : "❌ Azure Speech missing");

    var openaiStt = scope.ServiceProvider.GetService<OpenAITranscriptionService>();
    Console.WriteLine(openaiStt != null
        ? "✅ OpenAI Transcription (Whisper)"
        : "❌ OpenAI Transcription missing");

    var translator = scope.ServiceProvider.GetService<OpenAITranslatorService>();
    Console.WriteLine(translator != null
        ? "✅ OpenAI Translator (ar, fr, de, es, bn, zh - context-aware)"
        : "❌ Multi-language Translator missing");

    Console.WriteLine("\n🌍 Supported Languages:");
    Console.WriteLine("════════════════════════════════════════");
    Console.WriteLine("  • ar - Arabic (Modern Standard)");
    Console.WriteLine("  • fr - French");
    Console.WriteLine("  • de - German");
    Console.WriteLine("  • es - Spanish");
    Console.WriteLine("  • bn - Bangla (Bangladesh)");
    Console.WriteLine("  • zh - Mandarin Chinese (Simplified)");

    Console.WriteLine("\n💰 Cost Comparison (per 100-sentence story):");
    Console.WriteLine("════════════════════════════════════════");
    Console.WriteLine("OLD (Azure only):");
    Console.WriteLine("  • Azure STT: ~$0.40");
    Console.WriteLine("  • Azure Translator: ~$9.00");
    Console.WriteLine("  • Azure TTS: ~$0.40");
    Console.WriteLine("  • TOTAL: ~$9.80");
    Console.WriteLine();
    Console.WriteLine("NEW (OpenAI + Azure):");
    Console.WriteLine("  • OpenAI Whisper STT: ~$0.06");
    Console.WriteLine("  • OpenAI Translation (any language): ~$0.30");
    Console.WriteLine("  • Azure TTS (any language): ~$0.40");
    Console.WriteLine("  • TOTAL: ~$0.76");
    Console.WriteLine();
    Console.WriteLine("💵 SAVINGS: ~$9.04 per 100 stories (92% cheaper!)");
    Console.WriteLine("════════════════════════════════════════\n");
}

app.UseWebSockets();

// Swagger
app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();

// Test endpoint
app.MapGet("/", () => "VoiceTranslateMvp is running with Multi-Language Support 🌍🚀");

app.Map("/ws/student", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("WebSocket required");
        return;
    }

    var roomId = context.Request.Query["roomId"].ToString();
    if (string.IsNullOrWhiteSpace(roomId))
        roomId = "default";

    var lang = context.Request.Query["lang"].ToString();
    if (string.IsNullOrWhiteSpace(lang))
        lang = "ar";

    var supportedLanguages = new[] { "ar", "fr", "de", "es", "bn", "zh" };
    if (!supportedLanguages.Contains(lang.ToLower()))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync($"Unsupported language: {lang}. Supported: {string.Join(", ", supportedLanguages)}");
        return;
    }

    var ws = await context.WebSockets.AcceptWebSocketAsync();

    var connectionId = WsHub.AddStudent(roomId, ws, lang);
    Console.WriteLine($"✅ Student connected room='{roomId}' conn='{connectionId}' lang='{lang}'");

    // Keep socket open
    var buffer = new byte[1024];

    try
    {
        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
                break;

            // (Optional) if later you want to allow student to change language without reconnect,
            // parse JSON here and update WsHub.Rooms[roomId][connectionId].Language
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"⚠️ Student ws error: {ex.Message}");
    }
    finally
    {
        WsHub.RemoveStudent(roomId, connectionId);

        try
        {
            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { }

        Console.WriteLine($"📴 Student disconnected room='{roomId}' conn='{connectionId}'");
    }
}).DisableAntiforgery();




// =======================
// ✅ STEP 8: WebSocket: Teacher audio (passes language to session)
// =======================
app.Map("/ws/teacher-audio", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("WebSocket required");
        return;
    }

    var roomId = context.Request.Query["roomId"].ToString();
    if (string.IsNullOrWhiteSpace(roomId))
        roomId = "default";

    using var teacherWs = await context.WebSockets.AcceptWebSocketAsync();
    Console.WriteLine($"🎙️ Teacher connected room='{roomId}'");

    try
    {
        await VoiceSession.RunAsync(teacherWs, context.RequestServices, roomId);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Teacher session error: {ex.Message}");
    }

    Console.WriteLine($"📴 Teacher session ended room='{roomId}'");
}).DisableAntiforgery();

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Urls.Add($"http://0.0.0.0:{port}");
app.Run();