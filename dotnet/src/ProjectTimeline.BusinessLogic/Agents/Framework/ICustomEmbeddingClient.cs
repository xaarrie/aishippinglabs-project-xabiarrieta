namespace ProjectTimeline.BusinessLogic.Agents.Framework;

/// <summary>Numerical embeddings only; storage-specific vector types belong to persistence.</summary>
public interface ICustomEmbeddingClient
{
    Task<float[]> EmbedAsync(string input, CancellationToken ct);
}
