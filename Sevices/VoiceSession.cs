using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using VoiceTranslateMvp.DTO;

namespace VoiceTranslateMvp.Services
{
    public static class VoiceSession
    {
        // ✅ Per-room state (context + dedupe)
        private sealed class RoomState
        {
            public Queue<string> ContextWindow { get; } = new();
            public const int MaxContextSentences = 2;

            public string? LastEnglishText { get; set; }
            public DateTime LastEnglishTextAtUtc { get; set; } = DateTime.MinValue;
        }

        private static readonly ConcurrentDictionary<string, RoomState> _roomStates =
            new(StringComparer.OrdinalIgnoreCase);

        private static RoomState GetRoomState(string roomId) =>
            _roomStates.GetOrAdd(roomId, _ => new RoomState());

        public static void ClearRoomState(string roomId)
        {
            _roomStates.TryRemove(roomId, out _);
            Console.WriteLine($"🧹 Cleared room state for room '{roomId}'.");
        }

        private static async Task BroadcastJsonToRoomAsync(string roomId, object obj)
        {
            var students = WsHub.GetStudents(roomId);
            if (students.Count == 0) return;

            foreach (var student in students)
            {
                if (student.Ws.State != WebSocketState.Open)
                    continue;

                await SendJsonToStudentAsync(student, obj);
            }
        }
        private static async Task<string> ReceiveFullTextAsync(
    WebSocket ws, byte[] buffer, int firstCount, bool firstEnd, CancellationToken ct)
        {
            if (firstEnd)
                return Encoding.UTF8.GetString(buffer, 0, firstCount);

            using var ms = new MemoryStream();
            ms.Write(buffer, 0, firstCount);

            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                ms.Write(buffer, 0, r.Count);
            }
            while (!r.EndOfMessage);

            return Encoding.UTF8.GetString(ms.ToArray());
        }


        // ✅ Send to ONE student safely
        private static async Task SendJsonToStudentAsync(StudentConnection student, object obj)
        {
            if (student.Ws == null || student.Ws.State != WebSocketState.Open)
                return;

            var json = JsonSerializer.Serialize(obj);
            var bytes = Encoding.UTF8.GetBytes(json);

            await student.SendLock.WaitAsync();
            try
            {
                await student.Ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ SendJsonToStudentAsync failed ({student.ConnectionId}): {ex.Message}");
            }
            finally
            {
                student.SendLock.Release();
            }
        }

        public static async Task RunAsync(
            WebSocket inputWs,
            IServiceProvider sp,
            string roomId)
        {
            var speechService = sp.GetRequiredService<AzureSpeechService>();
            var sttService = sp.GetRequiredService<OpenAITranscriptionService>();
            var translator = sp.GetRequiredService<OpenAITranslatorService>();

            Console.WriteLine($"🎬 Teacher session started for room: {roomId}");

            long utteranceId = 0;

            // Audio assumptions
            const int SAMPLE_RATE = 16000;
            const int BYTES_PER_SAMPLE = 2;
            const int FRAME_MS = 20;
            const int FRAME_BYTES = (SAMPLE_RATE * FRAME_MS / 1000) * BYTES_PER_SAMPLE;

            // VAD settings
            const int PRE_ROLL_MS = 300;
            const int PRE_ROLL_FRAMES = PRE_ROLL_MS / FRAME_MS;

            const int SILENCE_MS_TO_FINALIZE = 300;
            const int MAX_SEGMENT_MS = 5000;
            const int MAX_WORDS_PER_SEGMENT = 12;

            const double RMS_SPEECH_THRESHOLD = 400;

            const int START_SPEECH_FRAMES = 2;
            const int END_SPEECH_FRAMES = 3;

            const int MIN_VOICED_FRAMES_TO_TRANSCRIBE = 6;
            const int MIN_SEGMENT_MS = 200;
            const double MIN_VOICED_RATIO = 0.15;

            const double MAX_RMS_FACTOR = 1.20;
            const double AVG_RMS_FACTOR = 0.80;

            // State
            var preRoll = new List<byte[]>(capacity: PRE_ROLL_FRAMES + 2);
            var speechFrames = new List<byte[]>(capacity: 1000);

            bool inSpeech = false;
            int consecutiveSpeechFrames = 0;
            int consecutiveSilentFrames = 0;
            int silenceMsSinceSpeech = 0;

            double estimatedWords = 0;

            var recvBuffer = new byte[8192];

            void PushPreRoll(byte[] frame)
            {
                preRoll.Add(frame);
                while (preRoll.Count > PRE_ROLL_FRAMES)
                    preRoll.RemoveAt(0);
            }

            void ResetSpeechState()
            {
                speechFrames.Clear();
                consecutiveSpeechFrames = 0;
                consecutiveSilentFrames = 0;
                silenceMsSinceSpeech = 0;
                inSpeech = false;
                estimatedWords = 0;
            }

            async Task FinalizeAndProcessIfValidAsync()
            {
                if (speechFrames.Count == 0)
                    return;

                int totalFrames = speechFrames.Count;
                int segmentMs = totalFrames * FRAME_MS;

                if (segmentMs < MIN_SEGMENT_MS)
                {
                    Console.WriteLine($"⚠️ Discarded segment: too short ({segmentMs}ms).");
                    return;
                }

                int voiced = 0;
                double maxRms = 0;
                double sumRms = 0;

                foreach (var f in speechFrames)
                {
                    var r = ComputePcm16Rms(f);
                    sumRms += r;
                    if (r > maxRms) maxRms = r;
                    if (r >= RMS_SPEECH_THRESHOLD) voiced++;
                }

                double avgRms = sumRms / Math.Max(1, totalFrames);
                double voicedRatio = voiced / (double)Math.Max(1, totalFrames);

                if (voiced < MIN_VOICED_FRAMES_TO_TRANSCRIBE)
                {
                    Console.WriteLine($"⚠️ Discarded segment: not enough voiced frames ({voiced}/{totalFrames}).");
                    return;
                }

                if (voicedRatio < MIN_VOICED_RATIO)
                {
                    Console.WriteLine($"⚠️ Discarded segment: voiced ratio too low ({voicedRatio:P0}).");
                    return;
                }

                if (maxRms < RMS_SPEECH_THRESHOLD * MAX_RMS_FACTOR || avgRms < RMS_SPEECH_THRESHOLD * AVG_RMS_FACTOR)
                {
                    Console.WriteLine($"⚠️ Discarded segment: looks like noise. max={maxRms:F0}, avg={avgRms:F0}, thr={RMS_SPEECH_THRESHOLD:F0}");
                    return;
                }

                var pcmBytes = ConcatFrames(speechFrames);
                var wavBytes = Pcm16ToWav(pcmBytes, SAMPLE_RATE, channels: 1);

                var englishText = await sttService.TranscribeEnglishFromWavBytesAsync(wavBytes);
                if (string.IsNullOrWhiteSpace(englishText))
                    return;

                englishText = englishText.Trim();

                // ✅ DEDUPE per room (fix repeated last sentence)
                var roomState = GetRoomState(roomId);
                var now = DateTime.UtcNow;

                if (!string.IsNullOrWhiteSpace(roomState.LastEnglishText) &&
                    string.Equals(roomState.LastEnglishText, englishText, StringComparison.OrdinalIgnoreCase) &&
                    (now - roomState.LastEnglishTextAtUtc).TotalSeconds < 2)
                {
                    Console.WriteLine("♻️ Skipping duplicate transcript (room dedupe).");
                    return;
                }

                roomState.LastEnglishText = englishText;
                roomState.LastEnglishTextAtUtc = now;

                Console.WriteLine($"🎤 Transcribed: '{englishText}'");

                var sentences = SplitIntoSentences(englishText);
                Console.WriteLine($"📝 Split into {sentences.Count} sentence(s)");

                foreach (var sentence in sentences)
                {
                    var currentSentence = sentence?.Trim();
                    if (string.IsNullOrWhiteSpace(currentSentence))
                        continue;

                    await BroadcastSentenceAsync(
                        roomId,
                        speechService,
                        translator,
                        currentSentence,
                        ++utteranceId);
                }
            }

            async Task BroadcastSentenceAsync(
                string roomId,
                AzureSpeechService speechService,
                OpenAITranslatorService translator,
                string englishSentence,
                long id)
            {
                var roomState = GetRoomState(roomId);

                // Context = shared English context per room
                var contextList = roomState.ContextWindow.ToList();

                // Update English context immediately
                roomState.ContextWindow.Enqueue(englishSentence);
                while (roomState.ContextWindow.Count > RoomState.MaxContextSentences)
                    roomState.ContextWindow.Dequeue();

                var students = WsHub.GetStudents(roomId);
                if (students.Count == 0)
                {
                    Console.WriteLine($"⚠️ No students in room '{roomId}' to broadcast to.");
                    return;
                }

                Console.WriteLine($"📡 Broadcasting utterance #{id} to {students.Count} students in room '{roomId}'.");

                // ✅ Translate + TTS per student language
                foreach (var student in students)
                {
                    if (student.Ws.State != WebSocketState.Open)
                        continue;

                    var targetLanguage = student.Language ?? "ar";

                    string translatedText;
                    try
                    {
                        translatedText = await translator.TranslateAsync(englishSentence, targetLanguage, contextList);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Translate failed for student {student.ConnectionId} lang {targetLanguage}: {ex.Message}");
                        continue;
                    }

                    // Text
                    await SendJsonToStudentAsync(student, new
                    {
                        type = "text",
                        id = id.ToString(),
                        en = englishSentence,
                        @out = translatedText,
                        lang = targetLanguage
                    });

                    // TTS + visemes
                    var tts = await speechService.SynthesizeWithVisemesAsync(translatedText, targetLanguage);

                    var audioBase64 = Convert.ToBase64String(tts.AudioWav);
                    await SendJsonToStudentAsync(student, new
                    {
                        type = "audio",
                        id = id.ToString(),
                        wavBase64 = audioBase64
                    });

                    var visemesList = tts.Visemes.Select(v => new
                    {
                        audioOffset = v.AudioOffsetTicks / 10000.0,
                        visemeId = v.VisemeId
                    }).ToList();

                    await SendJsonToStudentAsync(student, new
                    {
                        type = "visemes",
                        id = id.ToString(),
                        visemes = visemesList
                    });
                }
            }

            static List<string> SplitIntoSentences(string text)
            {
                var sentences = new List<string>();
                var parts = System.Text.RegularExpressions.Regex.Split(
                    text,
                    @"(?<=[.!?])\s+(?=[A-Z])|(?<=[.!?])$"
                );

                foreach (var part in parts)
                {
                    var trimmed = part.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                    {
                        if (!trimmed.EndsWith(".") && !trimmed.EndsWith("!") && !trimmed.EndsWith("?"))
                            trimmed += ".";
                        sentences.Add(trimmed);
                    }
                }

                if (sentences.Count == 0 && !string.IsNullOrWhiteSpace(text))
                {
                    var cleaned = text.Trim();
                    if (!cleaned.EndsWith(".") && !cleaned.EndsWith("!") && !cleaned.EndsWith("?"))
                        cleaned += ".";
                    sentences.Add(cleaned);
                }

                return sentences;
            }

            // Main loop
            while (inputWs.State == WebSocketState.Open)
            {
                var result = await inputWs.ReceiveAsync(new ArraySegment<byte>(recvBuffer), CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine("🔌 Teacher closed WebSocket");
                    break;
                }

                // Text messages: handle pose forwarding if teacher sends it
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    // Read full text message (in case it arrives fragmented)
                    var text = await ReceiveFullTextAsync(inputWs, recvBuffer, result.Count, result.EndOfMessage, CancellationToken.None);

                    // If teacher sends pose JSON, broadcast it to all students
                    if (!string.IsNullOrWhiteSpace(text) && text.StartsWith("{") && text.Contains("\"type\""))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(text);
                            var msgType = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;

                            if (msgType == "pose")
                            {
                                // ✅ Broadcast pose to everyone in the room
                                await BroadcastJsonToRoomAsync(roomId, doc.RootElement);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"⚠️ Pose parse/broadcast error: {ex.Message}");
                        }
                    }

                    continue;
                }


                if (result.Count == 0)
                    continue;

                var frame = new byte[result.Count];
                Buffer.BlockCopy(recvBuffer, 0, frame, 0, result.Count);

                if (frame.Length < FRAME_BYTES)
                    continue;

                var rms = ComputePcm16Rms(frame);
                bool isVoiced = (rms >= RMS_SPEECH_THRESHOLD);

                if (!inSpeech)
                {
                    PushPreRoll(frame);

                    if (isVoiced)
                    {
                        consecutiveSpeechFrames++;
                        consecutiveSilentFrames = 0;

                        if (consecutiveSpeechFrames >= START_SPEECH_FRAMES)
                        {
                            inSpeech = true;
                            speechFrames.AddRange(preRoll);
                            speechFrames.Add(frame);
                            preRoll.Clear();
                        }
                    }
                    else
                    {
                        consecutiveSpeechFrames = 0;
                    }
                }
                else
                {
                    speechFrames.Add(frame);
                    int totalMs = speechFrames.Count * FRAME_MS;

                    if (isVoiced)
                    {
                        consecutiveSilentFrames = 0;
                        silenceMsSinceSpeech = 0;
                        estimatedWords += 0.05;
                    }
                    else
                    {
                        consecutiveSilentFrames++;
                        silenceMsSinceSpeech += FRAME_MS;
                    }

                    bool shouldFinalize = false;

                    if (consecutiveSilentFrames >= END_SPEECH_FRAMES && silenceMsSinceSpeech >= SILENCE_MS_TO_FINALIZE)
                        shouldFinalize = true;

                    if (totalMs >= MAX_SEGMENT_MS)
                        shouldFinalize = true;

                    if (estimatedWords >= MAX_WORDS_PER_SEGMENT && silenceMsSinceSpeech >= 100)
                        shouldFinalize = true;

                    if (shouldFinalize)
                    {
                        await FinalizeAndProcessIfValidAsync();
                        ResetSpeechState();
                    }
                }
            }

            Console.WriteLine($"📴 Teacher session ended for room: {roomId}");
        }

        private static byte[] ConcatFrames(List<byte[]> frames)
        {
            int total = 0;
            foreach (var f in frames) total += f.Length;

            var result = new byte[total];
            int offset = 0;

            foreach (var f in frames)
            {
                Buffer.BlockCopy(f, 0, result, offset, f.Length);
                offset += f.Length;
            }

            return result;
        }

        private static double ComputePcm16Rms(ReadOnlySpan<byte> pcm16)
        {
            int samples = pcm16.Length / 2;
            if (samples <= 0) return 0;

            double sumSq = 0;
            for (int i = 0; i < samples; i++)
            {
                short s = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
                sumSq += (double)s * s;
            }
            return Math.Sqrt(sumSq / samples);
        }

        private static byte[] Pcm16ToWav(byte[] pcm16, int sampleRate, short channels)
        {
            int byteRate = sampleRate * channels * 2;
            short blockAlign = (short)(channels * 2);
            short bitsPerSample = 16;
            int subchunk2Size = pcm16.Length;
            int chunkSize = 36 + subchunk2Size;

            using var ms = new MemoryStream(44 + pcm16.Length);
            using var bw = new BinaryWriter(ms);

            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(chunkSize);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));

            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1);
            bw.Write(channels);
            bw.Write(sampleRate);
            bw.Write(byteRate);
            bw.Write(blockAlign);
            bw.Write(bitsPerSample);

            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(subchunk2Size);
            bw.Write(pcm16);

            bw.Flush();
            return ms.ToArray();
        }
    }
}
