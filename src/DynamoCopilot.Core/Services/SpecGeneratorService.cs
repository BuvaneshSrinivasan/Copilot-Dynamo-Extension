using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DynamoCopilot.Core.Models;

namespace DynamoCopilot.Core.Services
{
    /// <summary>
    /// Classifies a user message as a new code-generation request (SPEC) or a conversational
    /// follow-up (CHAT). When recent conversation history is supplied, the classifier uses it
    /// to correctly identify follow-ups ("fix it", "add error handling", "that code breaks")
    /// and only generates a spec card for genuinely new, independent code requests.
    /// </summary>
    public sealed class SpecGeneratorService
    {
        private const string SpecPrefix = "TYPE: SPEC|";
        private const string ChatPrefix = "TYPE: CHAT|";

        // How many recent assistant+user turns to include for context
        private const int HistoryTurnsForContext = 3;

        private readonly ILlmService _llm;

        public SpecGeneratorService(ILlmService llm)
        {
            _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        }

        /// <summary>
        /// Classify <paramref name="userText"/>.
        /// Pass <paramref name="recentHistory"/> (user+assistant messages from the current session)
        /// so the classifier can detect follow-ups and avoid false SPEC triggers.
        /// </summary>
        public async Task<SpecClassificationResult> ClassifyAsync(
            string userText,
            IList<ChatMessage>? recentHistory = null,
            CancellationToken ct = default)
        {
            bool hasHistory = recentHistory != null && recentHistory.Count > 0;
            var messages = BuildMessages(userText, recentHistory, hasHistory);

            var sb = new StringBuilder();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                await foreach (var token in _llm.SendStreamingAsync(messages, cts.Token))
                    sb.Append(token);
            }
            catch (OperationCanceledException)
            {
                return new SpecClassificationResult { IsSpec = false, ChatText = null };
            }
            catch
            {
                return new SpecClassificationResult { IsSpec = false, ChatText = null };
            }

            return Parse(sb.ToString().Trim());
        }

        private static List<ChatMessage> BuildMessages(
            string userText,
            IList<ChatMessage>? history,
            bool hasHistory)
        {
            var messages = new List<ChatMessage> { BuildSystemPrompt(hasHistory) };

            // Inject a summary of recent conversation turns so the classifier has context.
            // We trim to the last N user+assistant turns to keep the token cost low.
            if (history != null && history.Count > 0)
            {
                int take = HistoryTurnsForContext * 2; // each turn = 1 user + 1 assistant
                int start = Math.Max(0, history.Count - take);

                var contextSb = new StringBuilder();
                contextSb.AppendLine("[Recent conversation context — use this to decide if the new message is a follow-up or a new request]");
                for (int i = start; i < history.Count; i++)
                {
                    var msg = history[i];
                    if (msg.Role == ChatRole.System) continue;
                    string role = msg.Role == ChatRole.User ? "User" : "Assistant";
                    // Truncate long messages to keep tokens down
                    string content = msg.Content?.Length > 300
                        ? msg.Content.Substring(0, 300) + "..."
                        : msg.Content ?? string.Empty;
                    contextSb.AppendLine($"{role}: {content}");
                }

                messages.Add(new ChatMessage
                {
                    Role    = ChatRole.User,
                    Content = contextSb.ToString().Trim()
                });
                // Placeholder assistant ack so the message list is valid
                messages.Add(new ChatMessage
                {
                    Role    = ChatRole.Assistant,
                    Content = "Understood. I have the conversation context."
                });
            }

            messages.Add(new ChatMessage { Role = ChatRole.User, Content = userText });
            return messages;
        }

        private static SpecClassificationResult Parse(string raw)
        {
            if (raw.StartsWith(SpecPrefix, StringComparison.Ordinal))
            {
                string json = raw.Substring(SpecPrefix.Length).Trim();
                if (json.StartsWith("```")) json = StripCodeFences(json);
                try
                {
                    var spec = JsonSerializer.Deserialize<CodeSpecification>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (spec != null)
                        return new SpecClassificationResult { IsSpec = true, Spec = spec };
                }
                catch { }
            }

            if (raw.StartsWith(ChatPrefix, StringComparison.Ordinal))
                return new SpecClassificationResult
                {
                    IsSpec   = false,
                    ChatText = raw.Substring(ChatPrefix.Length).Trim()
                };

            return new SpecClassificationResult { IsSpec = false, ChatText = raw };
        }

        private static string StripCodeFences(string text)
        {
            int start = text.IndexOf('\n');
            int end   = text.LastIndexOf("```", StringComparison.Ordinal);
            if (start < 0 || end <= start) return text;
            return text.Substring(start + 1, end - start - 1).Trim();
        }

        private static ChatMessage BuildSystemPrompt(bool hasHistory) =>
            new ChatMessage
            {
                Role    = ChatRole.System,
                Content = hasHistory
                    ? BuildPromptWithHistory()
                    : BuildPromptFirstMessage()
            };

        // Used when there is NO prior conversation — every code request should be classified.
        private static string BuildPromptFirstMessage() => @"You are a Dynamo Python code-generation assistant inside Autodesk Dynamo for Revit.

Classify the user's message and respond accordingly.

## CLASSIFICATION

**CODE_REQUEST** — user wants a Python script or automation:
- Asking to write, create, generate, calculate, extract, modify, automate something
- Examples: ""calculate wall areas"", ""get all rooms on level 1"", ""write a script to rename sheets""

**CHAT** — everything else:
- Greetings, capability questions, general questions

## RESPONSE FORMAT

For CODE_REQUEST:
TYPE: SPEC|{""inputs"":[{""name"":""..."",""type"":""..."",""description"":""...""}],""steps"":[""Step 1"",""Step 2""],""output"":{""type"":""..."",""description"":""..."",""unit"":""""},""questions"":[{""question"":""..."",""options"":[""A"",""B""]}]}

For CHAT:
TYPE: CHAT|Your response here.

## SPEC RULES
- Never guess missing details — ask a clarifying question instead
- Steps: natural language only, no Revit API syntax
- Clear request → set ""questions"" to []
- Ambiguous → include 1–3 focused questions with concrete options

## ABSOLUTE RULES
1. Start with EXACTLY ""TYPE: SPEC|"" or ""TYPE: CHAT|"" — nothing before it
2. No markdown wrapper, no preamble
3. Never include Python code in a spec";

        // Used when conversation history exists — be much more conservative about triggering SPEC.
        private static string BuildPromptWithHistory() => @"You are a Dynamo Python code-generation assistant inside Autodesk Dynamo for Revit.

You will be given recent conversation context followed by a new user message.
Your job: decide if the new message is a FOLLOW-UP to the existing conversation or a BRAND NEW code request.

## DECISION RULE — THIS IS THE MOST IMPORTANT PART

Return TYPE: CHAT| for FOLLOW-UPS:
- Asking to fix, adjust, improve, or extend code that was already generated (""fix the error"", ""add error handling"", ""make it work for all levels"", ""that's not right"")
- Referring to existing code with pronouns (""it"", ""the script"", ""that code"", ""the result"")
- Error reports or tracebacks from running previous code
- Any clarification or follow-up on the previous response
- Questions about what was just generated

Return TYPE: SPEC| ONLY for BRAND NEW, INDEPENDENT requests:
- User explicitly starts a completely different, unrelated automation task
- The request has NO reference to previous code or conversation (no ""it"", ""that"", ""the"", ""fix"", ""adjust"")
- User signals a topic change (""now I need"", ""separately"", ""new task"", ""also write"")
- Even if mid-conversation, the request is self-contained and new

When in doubt → return TYPE: CHAT|

## RESPONSE FORMAT

For a BRAND NEW CODE_REQUEST:
TYPE: SPEC|{""inputs"":[{""name"":""..."",""type"":""..."",""description"":""...""}],""steps"":[""Step 1"",""Step 2""],""output"":{""type"":""..."",""description"":""..."",""unit"":""""},""questions"":[{""question"":""..."",""options"":[""A"",""B""]}]}

For a FOLLOW-UP or CHAT:
TYPE: CHAT|Your response here.

## SPEC RULES (only when returning SPEC)
- Steps: natural language only, no Revit API syntax
- Clear → ""questions"": []
- Ambiguous → 1–3 focused questions with concrete options

## ABSOLUTE RULES
1. Start with EXACTLY ""TYPE: SPEC|"" or ""TYPE: CHAT|"" — nothing before it
2. No preamble, no markdown, no explanation before the prefix
3. Never include Python code in a spec";
    }
}
