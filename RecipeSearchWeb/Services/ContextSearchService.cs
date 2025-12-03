using OpenAI.Embeddings;
using RecipeSearchWeb.Interfaces;
using RecipeSearchWeb.Models;
using ClosedXML.Excel;

namespace RecipeSearchWeb.Services;

/// <summary>
/// Service for searching context documents using vector embeddings
/// </summary>
public class ContextSearchService : IContextService
{
    private readonly EmbeddingClient _embeddingClient;
    private readonly ContextStorageService _storageService;
    private readonly ILogger<ContextSearchService> _logger;
    
    private List<ContextDocument> _documents = new();
    private List<ContextFile> _files = new();
    private bool _isInitialized = false;

    public ContextSearchService(
        EmbeddingClient embeddingClient,
        ContextStorageService storageService,
        ILogger<ContextSearchService> logger)
    {
        _embeddingClient = embeddingClient;
        _storageService = storageService;
        _logger = logger;
    }

    /// <summary>
    /// Initialize the service by loading documents from storage
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        try
        {
            await _storageService.InitializeAsync();
            _documents = await _storageService.LoadDocumentsAsync();
            _files = await _storageService.LoadFilesAsync();
            _isInitialized = true;
            _logger.LogInformation("Context search service initialized with {Count} documents from {FileCount} files", 
                _documents.Count, _files.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize context search service");
            _documents = new List<ContextDocument>();
            _files = new List<ContextFile>();
            _isInitialized = true;
        }
    }

    /// <summary>
    /// Import an Excel file into the context
    /// </summary>
    public async Task<(int imported, string message)> ImportExcelAsync(Stream fileStream, string fileName, string category, string uploadedBy)
    {
        await InitializeAsync();

        try
        {
            using var workbook = new XLWorkbook(fileStream);
            var worksheet = workbook.Worksheets.First();
            
            // Get headers from first row
            var headers = new Dictionary<int, string>();
            var headerRow = worksheet.Row(1);
            var lastColumn = worksheet.LastColumnUsed()?.ColumnNumber() ?? 1;
            
            for (int col = 1; col <= lastColumn; col++)
            {
                var headerValue = headerRow.Cell(col).GetString().Trim();
                if (!string.IsNullOrWhiteSpace(headerValue))
                {
                    headers[col] = headerValue;
                }
            }

            if (!headers.Any())
            {
                return (0, "No headers found in Excel file");
            }

            _logger.LogInformation("Found headers: {Headers}", string.Join(", ", headers.Values));

            // Map common column names
            var nameColumn = FindColumn(headers, "Name", "Title", "Nombre", "Titulo");
            var descColumn = FindColumn(headers, "Description", "Descripcion", "Desc", "Summary");
            var keywordsColumn = FindColumn(headers, "Keywords", "Tags", "Palabras clave", "Keywords/Tags");
            var linkColumn = FindColumn(headers, "Link", "URL", "Enlace", "Uri");

            // Parse rows
            var newDocuments = new List<ContextDocument>();
            var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
            
            for (int row = 2; row <= lastRow; row++) // Start from row 2 (skip headers)
            {
                var rowData = worksheet.Row(row);
                
                // Skip empty rows
                if (rowData.IsEmpty()) continue;

                var doc = new ContextDocument
                {
                    SourceFile = fileName,
                    Category = category,
                    Name = GetCellValue(rowData, nameColumn),
                    Description = GetCellValue(rowData, descColumn),
                    Keywords = GetCellValue(rowData, keywordsColumn),
                    Link = GetCellValue(rowData, linkColumn),
                    ImportedAt = DateTime.UtcNow
                };

                // Store additional columns
                foreach (var kvp in headers)
                {
                    if (kvp.Key != nameColumn && kvp.Key != descColumn && 
                        kvp.Key != keywordsColumn && kvp.Key != linkColumn)
                    {
                        var value = GetCellValue(rowData, kvp.Key);
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            doc.AdditionalData[kvp.Value] = value;
                        }
                    }
                }

                // Only add if there's meaningful content
                if (!string.IsNullOrWhiteSpace(doc.Name) || !string.IsNullOrWhiteSpace(doc.Description))
                {
                    newDocuments.Add(doc);
                }
            }

            if (!newDocuments.Any())
            {
                return (0, "No valid rows found in Excel file");
            }

            // Generate embeddings for new documents
            _logger.LogInformation("Generating embeddings for {Count} documents...", newDocuments.Count);
            
            foreach (var doc in newDocuments)
            {
                var searchText = doc.GetSearchableText();
                var embeddingResult = await _embeddingClient.GenerateEmbeddingAsync(searchText);
                doc.Embedding = embeddingResult.Value.ToFloats();
            }

            // Remove old documents from this file and add new ones
            _documents = _documents.Where(d => d.SourceFile != fileName).ToList();
            _documents.AddRange(newDocuments);

            // Update file metadata
            _files = _files.Where(f => f.FileName != fileName).ToList();
            _files.Add(new ContextFile
            {
                FileName = fileName,
                Category = category,
                EntryCount = newDocuments.Count,
                UploadedAt = DateTime.UtcNow,
                UploadedBy = uploadedBy
            });

            // Save to storage
            await _storageService.SaveDocumentsAsync(_documents);
            await _storageService.SaveFilesAsync(_files);

            _logger.LogInformation("Successfully imported {Count} documents from {FileName}", newDocuments.Count, fileName);
            return (newDocuments.Count, $"Successfully imported {newDocuments.Count} entries from {fileName}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing Excel file {FileName}", fileName);
            return (0, $"Error importing file: {ex.Message}");
        }
    }

    /// <summary>
    /// Search context documents using hybrid search (semantic + keyword)
    /// </summary>
    public async Task<List<ContextDocument>> SearchAsync(string query, int topResults = 5)
    {
        await InitializeAsync();

        if (!_documents.Any())
        {
            _logger.LogWarning("No context documents loaded");
            return new List<ContextDocument>();
        }

        try
        {
            var results = new List<(ContextDocument Doc, double Score)>();
            
            // STEP 1: Keyword/exact match search (for short terms like "IGA", codes, abbreviations)
            var keywordMatches = SearchByKeyword(query);
            foreach (var doc in keywordMatches)
            {
                results.Add((doc, 1.0)); // High score for exact matches
            }
            _logger.LogInformation("Keyword search for '{Query}' found {Count} matches", query, keywordMatches.Count);

            // STEP 2: Semantic search (for longer queries and contextual matching)
            var queryEmbedding = await _embeddingClient.GenerateEmbeddingAsync(query);
            var queryVector = queryEmbedding.Value.ToFloats();

            var semanticResults = _documents
                .Where(doc => !keywordMatches.Contains(doc)) // Exclude already found
                .Select(doc => new
                {
                    Document = doc,
                    Score = CosineSimilarity(queryVector, doc.Embedding)
                })
                .Where(x => x.Score > 0.2)
                .OrderByDescending(x => x.Score)
                .Take(topResults)
                .ToList();

            foreach (var item in semanticResults)
            {
                results.Add((item.Document, item.Score));
            }

            // Log top results for debugging
            _logger.LogInformation("Context search for '{Query}': Combined results: {Results}", 
                query, 
                string.Join(", ", results.Take(5).Select(r => $"{r.Doc.Name}:{r.Score:F3}")));

            // Return unique results, prioritizing higher scores
            var finalResults = results
                .GroupBy(r => r.Doc.Id)
                .Select(g => g.OrderByDescending(r => r.Score).First())
                .OrderByDescending(r => r.Score)
                .Take(topResults)
                .Select(r =>
                {
                    r.Doc.SearchScore = r.Score;
                    return r.Doc;
                })
                .ToList();

            _logger.LogInformation("Context search returned {Count} total results", finalResults.Count);
            return finalResults;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching context documents");
            return new List<ContextDocument>();
        }
    }

    /// <summary>
    /// Search by keyword/exact match (case-insensitive)
    /// Great for short terms, codes, abbreviations
    /// </summary>
    private List<ContextDocument> SearchByKeyword(string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2) // At least 2 characters
            .Select(t => t.ToLowerInvariant())
            .ToList();

        if (!terms.Any()) return new List<ContextDocument>();

        return _documents.Where(doc =>
        {
            var searchableText = $"{doc.Name} {doc.Description} {doc.Keywords}".ToLowerInvariant();
            
            // Check if any term matches exactly or as word boundary
            return terms.Any(term => 
                searchableText.Contains(term) ||
                // Also check Name field specifically for codes like "IGA"
                doc.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
        }).ToList();
    }

    /// <summary>
    /// Get all context files
    /// </summary>
    public async Task<List<ContextFile>> GetFilesAsync()
    {
        await InitializeAsync();
        return _files;
    }

    /// <summary>
    /// Get documents by source file
    /// </summary>
    public async Task<List<ContextDocument>> GetDocumentsByFileAsync(string fileName)
    {
        await InitializeAsync();
        return _documents.Where(d => d.SourceFile == fileName).ToList();
    }

    /// <summary>
    /// Delete a context file and its documents
    /// </summary>
    public async Task DeleteFileAsync(string fileName)
    {
        await InitializeAsync();
        
        _documents = _documents.Where(d => d.SourceFile != fileName).ToList();
        _files = _files.Where(f => f.FileName != fileName).ToList();
        
        await _storageService.SaveDocumentsAsync(_documents);
        await _storageService.SaveFilesAsync(_files);
        
        _logger.LogInformation("Deleted context file {FileName}", fileName);
    }

    /// <summary>
    /// Get total document count
    /// </summary>
    public int GetDocumentCount() => _documents.Count;

    /// <summary>
    /// Get all unique categories
    /// </summary>
    public async Task<List<string>> GetCategoriesAsync()
    {
        await InitializeAsync();
        return _files.Select(f => f.Category).Distinct().ToList();
    }

    #region Helper Methods

    private int FindColumn(Dictionary<int, string> headers, params string[] possibleNames)
    {
        foreach (var name in possibleNames)
        {
            var match = headers.FirstOrDefault(h => 
                h.Value.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                h.Value.Contains(name, StringComparison.OrdinalIgnoreCase));
            
            if (match.Key > 0)
                return match.Key;
        }
        return 0;
    }

    private string GetCellValue(IXLRow row, int column)
    {
        if (column <= 0) return string.Empty;
        return row.Cell(column).GetString().Trim();
    }

    private double CosineSimilarity(ReadOnlyMemory<float> a, ReadOnlyMemory<float> b)
    {
        var spanA = a.Span;
        var spanB = b.Span;
        
        if (spanA.Length != spanB.Length || spanA.Length == 0)
            return 0;

        double dotProduct = 0;
        double normA = 0;
        double normB = 0;

        for (int i = 0; i < spanA.Length; i++)
        {
            dotProduct += spanA[i] * spanB[i];
            normA += spanA[i] * spanA[i];
            normB += spanB[i] * spanB[i];
        }

        if (normA == 0 || normB == 0)
            return 0;

        return dotProduct / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    #endregion
}
