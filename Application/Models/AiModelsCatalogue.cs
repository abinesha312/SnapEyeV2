using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SnapEye.Models
{
    /// <summary>
    /// Single source of truth for the hardcoded list of AI providers + models the UI exposes.
    /// The backend mirrors these ids in <c>backend/services/llm_router.py</c>. Editing this
    /// catalogue requires a corresponding update on the Python side.
    /// </summary>
    public static class AiModelsCatalogue
    {
        /// <summary>Identifier used in JSON payloads and stored settings. Lowercase, stable.</summary>
        public const string ProviderOpenAI = "openai";
        public const string ProviderClaude = "anthropic";
        public const string ProviderGemini = "gemini";
        public const string ProviderGrok = "grok";

        /// <summary>User-visible metadata for one provider.</summary>
        public sealed class ProviderEntry
        {
            public string Id { get; init; } = "";
            public string DisplayName { get; init; } = "";
            public string Tagline { get; init; } = "";
            /// <summary>Single-letter monogram used in the provider card when no glyph is available.</summary>
            public string Monogram { get; init; } = "";
            /// <summary>Brand accent color for the card's gradient/border (hex including #).</summary>
            public string AccentHex { get; init; } = "#B794F7";
            /// <summary>Endpoint base URL shown as read-only helper text in the UI.</summary>
            public string EndpointUrl { get; init; } = "";
            /// <summary>Display hint for the API key field placeholder.</summary>
            public string ApiKeyPlaceholder { get; init; } = "";
            public IReadOnlyList<string> Models { get; init; } = new List<string>();
        }

        public static readonly ReadOnlyCollection<ProviderEntry> Providers = new(new List<ProviderEntry>
        {
            new ProviderEntry
            {
                Id = ProviderOpenAI,
                DisplayName = "OpenAI",
                Tagline = "GPT family",
                Monogram = "O",
                AccentHex = "#10A37F",
                EndpointUrl = "https://api.openai.com/v1",
                ApiKeyPlaceholder = "sk-...",
                Models = new List<string>
                {
                    "gpt-5",
                    "gpt-5-mini",
                    "gpt-5-nano",
                    "o3-deep-research",
                    "o4-mini-deep-research",
                    "gpt-4.1-mini",
                    "gpt-4",
                    "gpt-3.5-turbo",
                    "davinci-002",
                },
            },
            new ProviderEntry
            {
                Id = ProviderClaude,
                DisplayName = "Claude",
                Tagline = "Anthropic",
                Monogram = "C",
                AccentHex = "#D97757",
                EndpointUrl = "https://api.anthropic.com/v1",
                ApiKeyPlaceholder = "sk-ant-...",
                Models = new List<string>
                {
                    "claude-sonnet-4.5",
                    "claude-haiku-4.5",
                    "claude-opus-4.1",
                },
            },
            new ProviderEntry
            {
                Id = ProviderGemini,
                DisplayName = "Gemini",
                Tagline = "Google DeepMind",
                Monogram = "G",
                AccentHex = "#4285F4",
                EndpointUrl = "https://generativelanguage.googleapis.com/v1beta",
                ApiKeyPlaceholder = "AIza...",
                Models = new List<string>
                {
                    "gemini-2.5-pro",
                    "gemini-2.5-flash",
                    "gemini-2.5-flash-lite",
                    "gemini-2.0-flash-lite",
                },
            },
            new ProviderEntry
            {
                Id = ProviderGrok,
                DisplayName = "Grok",
                Tagline = "xAI",
                Monogram = "X",
                AccentHex = "#111111",
                EndpointUrl = "https://api.x.ai/v1",
                ApiKeyPlaceholder = "xai-...",
                Models = new List<string>
                {
                    "grok-4.20-reasoning",
                    "grok-4.20",
                },
            },
        });

        public static ProviderEntry? FindById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var p in Providers)
            {
                if (p.Id == id) return p;
            }
            return null;
        }
    }
}
