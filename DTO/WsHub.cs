using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace VoiceTranslateMvp.DTO
{
    public sealed class StudentConnection
    {
        public required string ConnectionId { get; init; }
        public required WebSocket Ws { get; init; }
        public required string Language { get; set; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }

    public static class WsHub
    {
        // roomId -> (connectionId -> student)
        public static ConcurrentDictionary<string, ConcurrentDictionary<string, StudentConnection>> Rooms
            = new(StringComparer.OrdinalIgnoreCase);

        public static StudentConnection? TryGetStudent(string roomId, string connectionId)
        {
            if (Rooms.TryGetValue(roomId, out var students) &&
                students.TryGetValue(connectionId, out var student))
                return student;

            return null;
        }

        public static IReadOnlyCollection<StudentConnection> GetStudents(string roomId)
        {
            if (Rooms.TryGetValue(roomId, out var students))
                return students.Values.ToList();

            return Array.Empty<StudentConnection>();
        }

        public static string AddStudent(string roomId, WebSocket ws, string lang)
        {
            var connectionId = Guid.NewGuid().ToString("N");

            var students = Rooms.GetOrAdd(roomId, _ => new ConcurrentDictionary<string, StudentConnection>());
            students[connectionId] = new StudentConnection
            {
                ConnectionId = connectionId,
                Ws = ws,
                Language = lang
            };

            return connectionId;
        }

        public static void RemoveStudent(string roomId, string connectionId)
        {
            if (!Rooms.TryGetValue(roomId, out var students))
                return;

            students.TryRemove(connectionId, out _);

            // cleanup empty room
            if (students.IsEmpty)
                Rooms.TryRemove(roomId, out _);
        }
    }
}
