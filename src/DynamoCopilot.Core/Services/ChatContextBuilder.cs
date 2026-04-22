using System;
using System.Collections.Generic;
using System.Linq;
using DynamoCopilot.Core.Models;

namespace DynamoCopilot.Core.Services
{
    /// <summary>
    /// Assembles the message list sent to the LLM on every turn.
    ///
    /// Strategy (same as GitHub Copilot / Claude Code):
    ///   1. System prompt always included first.
    ///   2. First <see cref="AnchorMessageCount"/> user/assistant messages are ALWAYS included
    ///      ("anchored") — this is where users typically give standing instructions
    ///      like "always use metric units" or "I'm targeting Revit 2024".
    ///   3. The remaining token budget is filled from the END of the history (most recent
    ///      turns), working backwards until the budget is exhausted.
    ///   4. If messages between the anchor and recent tail were skipped, a brief separator
    ///      is injected so the model knows context was condensed.
    ///
    /// Token counting is approximate: 1 token ≈ <see cref="CharsPerToken"/> characters.
    /// This is accurate enough for budget purposes without requiring a real tokenizer.
    /// </summary>
    public static class ChatContextBuilder
    {
        private const int CharsPerToken = 4;

        /// <summary>
        /// Default history token budget. Leaves room for:
        ///   - System prompt  (~2 000 tokens)
        ///   - RAG context    (~2 000 tokens)
        ///   - Model response (~4 000–8 000 tokens)
        /// on a 32k-token model. Modern models (GPT-4o, Gemini 2.5 Flash) have 128k+
        /// windows, so this budget is conservative and safe.
        /// </summary>
        public const int DefaultTokenBudget = 20_000;

        /// <summary>
        /// Number of messages at the start of the conversation that are ALWAYS included,
        /// regardless of how large the history has grown.
        /// </summary>
        public const int AnchorMessageCount = 8;

        /// <summary>
        /// Builds the full message list to send to the LLM for a normal chat turn.
        /// </summary>
        /// <param name="systemPrompt">The system/context prompt (prepended first).</param>
        /// <param name="sessionMessages">All messages in the current session.</param>
        /// <param name="tokenBudget">Maximum history tokens to include (excluding the system prompt).</param>
        public static List<ChatMessage> Build(
            ChatMessage systemPrompt,
            IList<ChatMessage> sessionMessages,
            int tokenBudget = DefaultTokenBudget)
        {
            var result = new List<ChatMessage> { systemPrompt };

            if (sessionMessages.Count == 0)
                return result;

            // Only user+assistant messages — system messages should not appear in session history
            var msgs = sessionMessages
                .Where(m => m.Role != ChatRole.System)
                .ToList();

            if (msgs.Count == 0)
                return result;

            // ── ANCHOR: first N messages always kept ──────────────────────────────
            int anchorCount = Math.Min(AnchorMessageCount, msgs.Count);
            var anchored    = msgs.Take(anchorCount).ToList();
            int anchorTokens = anchored.Sum(m => EstimateTokens(m.Content));

            // If the whole history fits in the anchor, we're done
            if (msgs.Count <= anchorCount)
            {
                result.AddRange(anchored);
                return result;
            }

            // ── RECENT TAIL: fill remaining budget from the end ───────────────────
            int remaining = tokenBudget - anchorTokens;
            var recent    = new List<ChatMessage>();

            for (int i = msgs.Count - 1; i >= anchorCount && remaining > 0; i--)
            {
                int tokens = EstimateTokens(msgs[i].Content);
                // Stop if this single message would exceed remaining budget
                // (but only stop if we already have some recent messages — don't skip
                // the very latest message even if it's large)
                if (recent.Count > 0 && tokens > remaining)
                    break;

                recent.Insert(0, msgs[i]);
                remaining -= tokens;
            }

            result.AddRange(anchored);

            // ── GAP MARKER: injected when messages were skipped ───────────────────
            int recentStartIndex = msgs.Count - recent.Count;
            if (recentStartIndex > anchorCount)
            {
                int skipped = recentStartIndex - anchorCount;
                result.Add(new ChatMessage
                {
                    Role    = ChatRole.User,
                    Content = $"[{skipped} earlier message(s) were condensed to fit the context window — " +
                               "your standing instructions above are always preserved]"
                });
                result.Add(new ChatMessage
                {
                    Role    = ChatRole.Assistant,
                    Content = "Understood. I'll continue with the preserved context."
                });
            }

            result.AddRange(recent);
            return result;
        }

        /// <summary>
        /// Builds a compact history list suitable for the spec classifier call.
        /// Includes anchored early messages (where instructions live) + the recent tail,
        /// with content truncated to keep classifier token cost low.
        /// </summary>
        /// <param name="sessionMessages">All messages in the current session.</param>
        /// <param name="maxTurns">Total number of turns to include (anchor + recent combined).</param>
        /// <param name="maxCharsPerMessage">Content is truncated to this many characters per message.</param>
        public static IList<ChatMessage> BuildForClassifier(
            IList<ChatMessage> sessionMessages,
            int maxTurns           = 10,
            int maxCharsPerMessage = 600)
        {
            var msgs = sessionMessages
                .Where(m => m.Role != ChatRole.System)
                .ToList();

            if (msgs.Count == 0)
                return msgs;

            // Anchor: first 2 messages (usually the first user instruction + assistant response)
            int anchorCount = Math.Min(2, msgs.Count);
            // Recent tail: fill remaining slots
            int recentCount = Math.Min(maxTurns - anchorCount, msgs.Count - anchorCount);

            IEnumerable<ChatMessage> selected = msgs.Take(anchorCount);
            if (recentCount > 0)
                selected = selected.Concat(msgs.Skip(Math.Max(anchorCount, msgs.Count - recentCount)));

            // Truncate content to keep classifier token cost low,
            // but preserve more of the content than the old 300-char limit
            return selected
                .Select(m => (m.Content?.Length ?? 0) > maxCharsPerMessage
                    ? new ChatMessage
                      {
                          Role      = m.Role,
                          Content   = m.Content!.Substring(0, maxCharsPerMessage) + "…",
                          Timestamp = m.Timestamp
                      }
                    : m)
                .ToList();
        }

        /// <summary>
        /// Returns true if the message at <paramref name="index"/> would be included
        /// by <see cref="Build"/> with the given budget.
        /// Used by the fix-error path to avoid re-sending code the LLM already has.
        /// </summary>
        public static bool IsMessageInContext(
            IList<ChatMessage> sessionMessages,
            int index,
            int tokenBudget = DefaultTokenBudget)
        {
            if (index < 0 || index >= sessionMessages.Count)
                return false;

            var msgs = sessionMessages
                .Where(m => m.Role != ChatRole.System)
                .ToList();

            // Anchored messages are always in context
            if (index < AnchorMessageCount)
                return true;

            // For the tail: walk backwards and see if index is reached before budget runs out
            int anchorTokens = msgs
                .Take(Math.Min(AnchorMessageCount, msgs.Count))
                .Sum(m => EstimateTokens(m.Content));
            int remaining = tokenBudget - anchorTokens;

            for (int i = msgs.Count - 1; i >= AnchorMessageCount && remaining > 0; i--)
            {
                remaining -= EstimateTokens(msgs[i].Content);
                if (i == index)
                    return remaining >= 0; // was there still budget when we reached it?
            }

            return false;
        }

        /// <summary>Rough token estimate: 1 token ≈ 4 characters.</summary>
        public static int EstimateTokens(string? text) =>
            (text?.Length ?? 0) / CharsPerToken;
    }
}
