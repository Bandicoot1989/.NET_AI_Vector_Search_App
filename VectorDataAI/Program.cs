using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;
using VectorDataAI;

// Load the configuration values.
IConfigurationRoot config = new ConfigurationBuilder().AddUserSecrets<Program>().Build();
string endpoint = config["AZURE_OPENAI_ENDPOINT"] ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT is not set");
string model = config["AZURE_OPENAI_GPT_NAME"] ?? throw new InvalidOperationException("AZURE_OPENAI_GPT_NAME is not set");
string apiKey = config["AZURE_OPENAI_API_KEY"] ?? throw new InvalidOperationException("AZURE_OPENAI_API_KEY is not set");

// Create the Azure OpenAI client
var azureClient = new AzureOpenAIClient(new Uri(endpoint), new System.ClientModel.ApiKeyCredential(apiKey));
var embeddingClient = azureClient.GetEmbeddingClient(model);

// Define the list of cloud services
List<CloudService> cloudServices =
[
    new() {
            Key = 0,
            Name = "Azure App Service",
            Description = "Host .NET, Java, Node.js, and Python web applications and APIs in a fully managed Azure service. You only need to deploy your code to Azure. Azure takes care of all the infrastructure management like high availability, load balancing, and autoscaling."
    },
    new() {
            Key = 1,
            Name = "Azure Service Bus",
            Description = "A fully managed enterprise message broker supporting both point to point and publish-subscribe integrations. It's ideal for building decoupled applications, queue-based load leveling, or facilitating communication between microservices."
    },
    new() {
            Key = 2,
            Name = "Azure Blob Storage",
            Description = "Azure Blob Storage allows your applications to store and retrieve files in the cloud. Azure Storage is highly scalable to store massive amounts of data and data is stored redundantly to ensure high availability."
    },
    new() {
            Key = 3,
            Name = "Microsoft Entra ID",
            Description = "Manage user identities and control access to your apps, data, and resources."
    },
    new() {
            Key = 4,
            Name = "Azure Key Vault",
            Description = "Store and access application secrets like connection strings and API keys in an encrypted vault with restricted access to make sure your secrets and your application aren't compromised."
    },
    new() {
            Key = 5,
            Name = "Azure AI Search",
            Description = "Information retrieval at scale for traditional and conversational search applications, with security and options for AI enrichment and vectorization."
    }
];

// Generate embeddings for all services
Console.WriteLine("Generating embeddings...\n");
foreach (CloudService service in cloudServices)
{
    var embeddingResponse = await embeddingClient.GenerateEmbeddingsAsync(new List<string> { service.Description });
    service.Vector = embeddingResponse.Value[0].ToFloats();
    Console.WriteLine($"Added: {service.Name}");
}

// Interactive search loop
Console.WriteLine("\n=== Azure Service Vector Search ===");
Console.WriteLine("Enter a search query (or 'exit' to quit):\n");

while (true)
{
    Console.Write("> ");
    string? userQuery = Console.ReadLine();
    
    if (string.IsNullOrWhiteSpace(userQuery) || userQuery.Equals("exit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    // Generate embedding for the search query
    var queryEmbeddingResponse = await embeddingClient.GenerateEmbeddingsAsync(new List<string> { userQuery });
    var queryVector = queryEmbeddingResponse.Value[0].ToFloats().ToArray();

    // Perform manual similarity search
    var similarities = cloudServices.Select(service => new
    {
        Service = service,
        Score = CosineSimilarity(queryVector, service.Vector.ToArray())
    })
    .OrderByDescending(x => x.Score)
    .Take(3);

    Console.WriteLine($"\nTop 3 results for '{userQuery}':\n");
    
    foreach (var result in similarities)
    {
        Console.WriteLine($"  [{result.Score:F4}] {result.Service.Name}");
        Console.WriteLine($"  {result.Service.Description}\n");
    }
}

// Helper method to calculate cosine similarity
static double CosineSimilarity(float[] vector1, float[] vector2)
{
    if (vector1.Length != vector2.Length)
        throw new ArgumentException("Vectors must have the same length");

    double dotProduct = 0;
    double magnitude1 = 0;
    double magnitude2 = 0;

    for (int i = 0; i < vector1.Length; i++)
    {
        dotProduct += vector1[i] * vector2[i];
        magnitude1 += vector1[i] * vector1[i];
        magnitude2 += vector2[i] * vector2[i];
    }

    magnitude1 = Math.Sqrt(magnitude1);
    magnitude2 = Math.Sqrt(magnitude2);

    if (magnitude1 == 0 || magnitude2 == 0)
        return 0;

    return dotProduct / (magnitude1 * magnitude2);
}