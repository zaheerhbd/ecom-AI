using System.Collections.Generic;
using System.Linq;
using Core.Models;

namespace Infrastructure.Services
{
    /// <summary>
    /// Builder for constructing OpenAI prompts with configurable components.
    /// </summary>
    /// <example>
    /// <para>Example final prompt output:</para>
    /// <code>
    /// You are AI-Lab, a shopping assistant for this e-commerce store.
    /// Answer the customer using only the provided sources.
    /// Do not invent products, prices, delivery options, discounts, policies, locations, warranties, or payment methods.
    /// If the sources do not support an answer, say you could not confirm it from the store data.
    /// Keep the answer concise and customer-friendly.
    /// When products or delivery options are listed, include useful names and prices when present.
    /// Return plain text only.
    ///
    /// Customer question:
    /// What is the cheapest delivery option?
    ///
    /// Sources:
    /// Source 1
    /// Title: Shipping Policy
    /// Type: Policy
    /// Text: Standard delivery costs $5.99 and takes 5-7 business days...
    /// Metadata: category:shipping, last-updated:2026-01-15
    ///
    /// Source 2
    /// Title: Express Delivery
    /// Type: Policy
    /// Text: Express shipping costs $12.99 and takes 2-3 business days...
    /// Metadata: category:shipping, last-updated:2026-01-15
    /// </code>
    /// </example>
    public class OpenAiPromptBuilder : IPromptBuilder
    {
        private string _systemRole = "You are AI-Lab, a shopping assistant for this e-commerce store.";
        private readonly List<string> _constraints = new();
        private string _fallbackInstruction = "If the sources do not support an answer, say you could not confirm it from the store data.";
        private readonly List<string> _formattingRules = new();
        private string _question = "";
        private readonly List<AiDocumentChunk> _sources = new();

        public IPromptBuilder WithSystemRole(string role)
        {
            _systemRole = role;
            return this;
        }

        public IPromptBuilder WithConstraints(IEnumerable<string> constraints)
        {
            _constraints.Clear();
            _constraints.AddRange(constraints);
            return this;
        }

        public IPromptBuilder WithFallbackInstruction(string instruction)
        {
            _fallbackInstruction = instruction;
            return this;
        }

        public IPromptBuilder WithFormattingRules(IEnumerable<string> rules)
        {
            _formattingRules.Clear();
            _formattingRules.AddRange(rules);
            return this;
        }

        public IPromptBuilder WithQuestion(string question)
        {
            _question = question;
            return this;
        }

        public IPromptBuilder WithSources(IEnumerable<AiDocumentChunk> sources)
        {
            _sources.Clear();
            _sources.AddRange(sources);
            return this;
        }

        /// <summary>
        /// Builds the final prompt string to send to OpenAI.
        /// </summary>
        /// <returns>
        /// Complete prompt with the following structure:
        /// <code>
        /// [System Role]
        /// Answer the customer using only the provided sources.
        /// [Constraints]
        /// [Fallback Instruction]
        /// [Formatting Rules]
        ///
        /// Customer question:
        /// [User Question]
        ///
        /// Sources:
        /// [Formatted Source Blocks]
        /// </code>
        /// </returns>
        public string Build()
        {
            var constraints = _constraints.Count > 0
                ? string.Join("\n", _constraints)
                : "Do not invent products, prices, delivery options, discounts, policies, locations, warranties, or payment methods.";

            var formatting = _formattingRules.Count > 0
                ? string.Join("\n", _formattingRules)
                : "Keep the answer concise and customer-friendly.\nWhen products or delivery options are listed, include useful names and prices when present.\nReturn plain text only.";

            var sourceBlocks = _sources.Select((s, i) => FormatSource(i, s));

            return $"{_systemRole}\n" +
                   "Answer the customer using only the provided sources.\n" +
                   $"{constraints}\n" +
                   $"{_fallbackInstruction}\n" +
                   $"{formatting}\n\n" +
                   $"Customer question:\n{_question}\n\n" +
                   $"Sources:\n{string.Join("\n\n", sourceBlocks)}";
        }

        private static string FormatSource(int index, AiDocumentChunk source)
        {
            var metadata = source.Metadata?.Count > 0
                ? string.Join(", ", source.Metadata.Select(m => $"{m.Key}: {m.Value}"))
                : "none";

            return $"Source {index + 1}\n" +
                   $"Title: {source.Title}\n" +
                   $"Type: {source.SourceType}\n" +
                   $"Text: {source.Text}\n" +
                   $"Metadata: {metadata}";
        }
    }

    public interface IPromptBuilder
    {
        IPromptBuilder WithSystemRole(string role);
        IPromptBuilder WithConstraints(IEnumerable<string> constraints);
        IPromptBuilder WithFallbackInstruction(string instruction);
        IPromptBuilder WithFormattingRules(IEnumerable<string> rules);
        IPromptBuilder WithQuestion(string question);
        IPromptBuilder WithSources(IEnumerable<AiDocumentChunk> sources);
        string Build();
    }
}