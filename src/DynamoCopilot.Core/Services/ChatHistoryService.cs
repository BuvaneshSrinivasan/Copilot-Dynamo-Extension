using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DynamoCopilot.Core.Models;

namespace DynamoCopilot.Core.Services
{
    /// <summary>
    /// Loads and saves chat session history per .dyn graph file.
    /// Sessions are stored in: %APPDATA%\DynamoCopilot\history\{hash_of_path}.json
    /// </summary>
    public sealed class ChatHistoryService
    {
        private readonly string _historyDirectory;

        public ChatHistoryService()
        {
            _historyDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DynamoCopilot",
                "history");

            Directory.CreateDirectory(_historyDirectory);
        }

        /// <summary>
        /// Loads the session for the given graph file path.
        /// Returns a new empty session if none exists.
        /// </summary>
        public ChatSession Load(string graphFilePath)
        {
            if (string.IsNullOrWhiteSpace(graphFilePath))
                return CreateNew(graphFilePath ?? string.Empty);

            var filePath = GetSessionFilePath(graphFilePath);
            if (!File.Exists(filePath))
                return CreateNew(graphFilePath);

            try
            {
                var json = File.ReadAllText(filePath, Encoding.UTF8);
                var session = JsonSerializer.Deserialize<ChatSession>(json);
                return session ?? CreateNew(graphFilePath);
            }
            catch (Exception)
            {
                // Corrupted file — start fresh
                return CreateNew(graphFilePath);
            }
        }

        /// <summary>
        /// Saves the session to disk.
        /// </summary>
        public void Save(ChatSession session)
        {
            if (session == null) return;

            session.LastModifiedAt = DateTime.UtcNow;
            var filePath = GetSessionFilePath(session.GraphFilePath);

            var json = JsonSerializer.Serialize(session, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(filePath, json, Encoding.UTF8);
        }

        /// <summary>
        /// Deletes the persisted session for a given graph (e.g. when clearing history).
        /// </summary>
        public void DeleteSession(string graphFilePath)
        {
            var filePath = GetSessionFilePath(graphFilePath);
            if (File.Exists(filePath))
                File.Delete(filePath);
        }

        private static ChatSession CreateNew(string graphFilePath) =>
            new ChatSession
            {
                GraphFilePath = graphFilePath,
                Messages = new List<ChatMessage>(),
                CreatedAt = DateTime.UtcNow,
                LastModifiedAt = DateTime.UtcNow
            };

        private string GetSessionFilePath(string graphFilePath)
        {
            // Use a SHA-256 hash of the absolute path as the filename
            // to avoid filesystem-illegal characters and path length issues.
            var hash = ComputeSha256(graphFilePath.ToLowerInvariant());
            return Path.Combine(_historyDirectory, hash + ".json");
        }

        private static string ComputeSha256(string input)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            var sb = new StringBuilder(64);
            foreach (var b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
