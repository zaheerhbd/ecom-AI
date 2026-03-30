using System.Collections.Generic;

namespace Core.Models
{
    public class AiDocumentChunk
    {
        // Unique id for this small searchable piece.
        // Example: "product-3-chunk-2"
        public string Id { get; set; }

        // Id of the original full document this chunk came from.
        // Example: many chunks can all belong to the same product document.
        public string DocumentId { get; set; }

        // Tells us what kind of source this is.
        // Example: "product" or "policy"
        public string SourceType { get; set; }

        // Human-friendly title shown in search results and sources.
        public string Title { get; set; }

        // The actual text content of this chunk.
        // Example: one sentence from a product description.
        public string Text { get; set; }

        // Extra structured fields we want to keep with the chunk.
        // Example: brand, type, price, url
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
    }
}
