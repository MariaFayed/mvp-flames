using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using System.Text;

namespace VoiceTranslateMvp.Services;

public class AzureSpeechService
{
    private readonly string _speechKey;
    private readonly string _speechRegion;
    public record VisemeItem(long AudioOffsetTicks, int VisemeId);
    public record TtsWithVisemes(byte[] AudioWav, List<VisemeItem> Visemes);

    // ✅ STEP 1: Language to Azure Neural Voice mapping (all support visemes)
    private static readonly Dictionary<string, (string VoiceName, string Locale)> LanguageVoiceMap = new()
    {
        { "ar", ("ar-EG-SalmaNeural", "ar-EG") },          // Arabic (MSA)
        { "fr", ("fr-FR-DeniseNeural", "fr-FR") },         // French
        { "de", ("de-DE-KatjaNeural", "de-DE") },          // German
        { "es", ("es-ES-ElviraNeural", "es-ES") },         // Spanish
        { "bn", ("bn-IN-TanishaaNeural", "bn-IN") },       // Bangla
        { "zh", ("zh-CN-XiaoxiaoNeural", "zh-CN") }        // Mandarin Chinese
    };

    public AzureSpeechService(IConfiguration configuration)
    {
        _speechKey = configuration["AzureSpeech:Key"]
                     ?? throw new Exception("Missing AzureSpeech:Key in configuration");
        _speechRegion = configuration["AzureSpeech:Region"]
                        ?? throw new Exception("Missing AzureSpeech:Region in configuration");
    }

    private SpeechConfig CreateSpeechConfig()
    {
        return SpeechConfig.FromSubscription(_speechKey, _speechRegion);
    }

    public SpeechConfig CreateSpeechConfigForRealTime()
    {
        var config = CreateSpeechConfig();
        config.SpeechRecognitionLanguage = "en-US";

        // Optimize for real-time
        config.SetProperty(PropertyId.SpeechServiceConnection_InitialSilenceTimeoutMs, "3000");
        config.SetProperty(PropertyId.SpeechServiceConnection_EndSilenceTimeoutMs, "500");
        config.SetProperty(PropertyId.SpeechServiceConnection_EnableAudioLogging, "true");
        config.OutputFormat = OutputFormat.Detailed;

        // Enable word-level timing for better accuracy
        config.RequestWordLevelTimestamps();

        return config;
    }

    /// <summary>
    /// Recognize English speech from a WAV file on disk.
    /// </summary>
    public async Task<string> RecognizeEnglishFromFileAsync(string audioFilePath)
    {
        if (!File.Exists(audioFilePath))
            throw new FileNotFoundException("Audio file not found", audioFilePath);

        var config = CreateSpeechConfig();
        config.SpeechRecognitionLanguage = "en-US";

        using var audioConfig = AudioConfig.FromWavFileInput(audioFilePath);
        using var recognizer = new SpeechRecognizer(config, audioConfig);

        var fullText = new StringBuilder();
        var stopRecognition = new TaskCompletionSource<int>();

        recognizer.Recognizing += (s, e) =>
        {
            // This fires with partial results – you can log if you want
            // Console.WriteLine($"[Partial] {e.Result.Text}");
        };

        recognizer.Recognized += (s, e) =>
        {
            if (e.Result.Reason == ResultReason.RecognizedSpeech)
            {
                if (!string.IsNullOrWhiteSpace(e.Result.Text))
                {
                    fullText.AppendLine(e.Result.Text);
                }
            }
            else if (e.Result.Reason == ResultReason.NoMatch)
            {
                Console.WriteLine("[STT] NoMatch");
            }
        };

        recognizer.Canceled += (s, e) =>
        {
            Console.WriteLine($"[STT] Canceled: {e.Reason}, {e.ErrorDetails}");
            stopRecognition.TrySetResult(0);
        };

        recognizer.SessionStopped += (s, e) =>
        {
            Console.WriteLine("[STT] SessionStopped");
            stopRecognition.TrySetResult(0);
        };

        await recognizer.StartContinuousRecognitionAsync();
        await stopRecognition.Task; // waits until SessionStopped or Canceled
        await recognizer.StopContinuousRecognitionAsync();

        var resultText = fullText.ToString().Trim();
        if (string.IsNullOrWhiteSpace(resultText))
            throw new Exception("Speech recognition produced no text.");

        return resultText;
    }


    /// <summary>
    /// Synthesize Arabic speech (ar-EG) to WAV bytes from the given text.
    /// DEPRECATED: Use SynthesizeWithVisemesAsync instead for multi-language support
    /// </summary>
    public async Task<byte[]> SynthesizeArabicAsync(string arabicText)
    {
        var config = CreateSpeechConfig();

        // The voice must support styles; if not, we'll fall back
        var voiceName = "ar-EG-SalmaNeural";
        config.SpeechSynthesisVoiceName = voiceName;


        // Add required namespace for mstts
        var ssml = @"<speak version=""1.0"" 
                    xmlns=""http://www.w3.org/2001/10/synthesis"" 
                    xmlns:mstts=""https://www.w3.org/2001/mstts"" 
                    xml:lang=""ar-EG"">" +
                $@"<voice name=""{voiceName}"">
                <mstts:express-as style=""chat"">
                    {System.Security.SecurityElement.Escape(arabicText)}
                </mstts:express-as>
              </voice>
            </speak>";

        using var synthesizer = new SpeechSynthesizer(config, null as AudioConfig);
        var result = await synthesizer.SpeakSsmlAsync(ssml);

        if (result.Reason != ResultReason.SynthesizingAudioCompleted)
            throw new Exception($"TTS failed: {result.Reason}");

        return result.AudioData;
    }


    public SpeechConfig CreateSpeechConfigForStreaming()
    {
        var config = CreateSpeechConfig();
        config.SpeechRecognitionLanguage = "en-US";

        return config;
    }


    /// <summary>
    /// ✅ STEP 2: Updated method - Synthesize speech with visemes for ANY supported language
    /// </summary>
    public async Task<TtsWithVisemes> SynthesizeWithVisemesAsync(string text, string languageCode = "ar")
    {
        // Validate language support
        if (!LanguageVoiceMap.TryGetValue(languageCode, out var voiceInfo))
        {
            throw new Exception($"Unsupported language: {languageCode}. Supported: {string.Join(", ", LanguageVoiceMap.Keys)}");
        }

        var config = CreateSpeechConfig();
        config.SpeechSynthesisVoiceName = voiceInfo.VoiceName;

        var visemes = new List<VisemeItem>();

        using var synthesizer = new SpeechSynthesizer(config, null);

        synthesizer.VisemeReceived += (_, e) =>
        {
            visemes.Add(new VisemeItem((long)e.AudioOffset, (int)e.VisemeId));
        };

        // Use SSML with chat style for natural speech
        var ssml = $@"
<speak version='1.0'
       xmlns='http://www.w3.org/2001/10/synthesis'
       xmlns:mstts='https://www.w3.org/2001/mstts'
       xml:lang='{voiceInfo.Locale}'>
  <voice name='{voiceInfo.VoiceName}'>
    <mstts:express-as style='chat'>
      {System.Security.SecurityElement.Escape(text)}
    </mstts:express-as>
  </voice>
</speak>";

        var result = await synthesizer.SpeakSsmlAsync(ssml);

        if (result.Reason != ResultReason.SynthesizingAudioCompleted)
            throw new Exception($"TTS failed: {result.Reason}");

        Console.WriteLine($"[Azure TTS] Generated audio for '{languageCode}' ({voiceInfo.VoiceName}): {result.AudioData.Length} bytes, {visemes.Count} visemes");

        return new TtsWithVisemes(result.AudioData, visemes);
    }

    // ✅ LEGACY: Keep old method name for backward compatibility
    public async Task<TtsWithVisemes> SynthesizeArabicWithVisemesAsync(string arabicText)
    {
        return await SynthesizeWithVisemesAsync(arabicText, "ar");
    }
}