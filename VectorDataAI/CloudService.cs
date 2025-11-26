using Microsoft.Extensions.VectorData;

namespace VectorDataAI;

public class CloudService
{
    [VectorStoreKey]
    public int Key { get; set; }

    [VectorStoreData]
    public required string Name { get; set; }

    [VectorStoreData]
    public required string Description { get; set; }

    [VectorStoreVector(
        Dimensions: 384,
        DistanceFunction = DistanceFunction.CosineSimilarity)]
        public ReadOnlyMemory<float> Vector { get; set; }
}
