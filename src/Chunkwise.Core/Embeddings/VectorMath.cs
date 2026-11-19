namespace Chunkwise.Core.Embeddings;

/// <summary>
/// The only vector maths RAG needs. Plain loops on purpose: for a few hundred 768-dimension vectors
/// they run in microseconds, and nobody has to learn a SIMD library to read them.
/// </summary>
public static class VectorMath
{
    /// <summary>Scales <paramref name="vector"/> in place to unit length (no-op for the zero vector).</summary>
    /// <remarks>
    /// Normalising once at write time means cosine similarity at query time is just a dot product,
    /// which is faster and one less thing to get wrong.
    /// </remarks>
    public static float[] Normalize(float[] vector)
    {
        double sumOfSquares = 0;
        foreach (var x in vector) sumOfSquares += x * x;
        if (sumOfSquares == 0) return vector;

        float inverse = (float)(1.0 / Math.Sqrt(sumOfSquares));
        for (int i = 0; i < vector.Length; i++) vector[i] *= inverse;
        return vector;
    }

    /// <summary>Dot product; equal to cosine similarity when both vectors are unit length.</summary>
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException($"Vector dimensions differ ({a.Length} vs {b.Length}). Was the index built with a different embedder?");

        double sum = 0;
        for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return (float)sum;
    }
}
