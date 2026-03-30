namespace Core.Models
{
    public class AiSearchMatch
    {
        // The chunk that retrieval thinks is relevant to the user's question.
        public AiDocumentChunk Chunk { get; set; }

        // Numeric similarity score.
        // Higher score means the question and chunk look more alike.
        public double Score { get; set; }
    }
}
