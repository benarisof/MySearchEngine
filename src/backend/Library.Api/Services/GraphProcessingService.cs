using Library.Api.Data;
using Library.Api.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Collections.Concurrent;
using System.Diagnostics; 
using System.Text.RegularExpressions;

namespace Library.Api.Services;

/// <summary>
/// Service applicatif dont le but est de construire un graphe de similarité entre livres à partir de leur contenu 
/// textuel, puis d’y calculer un score PageRank et de mettre à jour la base de données avec les résultats.
/// </summary>
public class GraphProcessingService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly GraphProcessingOptions _options;
    private readonly ILogger<GraphProcessingService> _logger;

    private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.Ordinal)
    {
        // Anglais
        "i", "me", "my", "myself", "we", "our", "ours", "ourselves", "you", "your", "yours", "yourself", "yourselves",
        "he", "him", "his", "himself", "she", "her", "hers", "herself", "it", "its", "itself",
        "they", "them", "their", "theirs", "themselves", "what", "which", "who", "whom", "this", "that", "these", "those",
        "am", "is", "are", "was", "were", "be", "been", "being", "have", "has", "had", "having", "do", "does", "did", "doing",
        "a", "an", "the", "and", "but", "if", "or", "because", "as", "until", "while", "of", "at", "by", "for", "with",
        "about", "against", "between", "into", "through", "during", "before", "after", "above", "below", "to", "from",
        "up", "down", "in", "out", "on", "off", "over", "under", "again", "further", "then", "once", "here", "there",
        "when", "where", "why", "how", "all", "any", "both", "each", "few", "more", "most", "other", "some", "such",
        "no", "nor", "not", "only", "own", "same", "so", "than", "too", "very", "s", "t", "can", "will", "just", "don",
        "should", "now",
        // Français
        "le", "la", "les", "de", "du", "des", "et", "un", "une", "à", "au", "aux", "pour", "dans", "sur", "par", "avec",
        "est", "sont", "qui", "que", "quoi", "dont", "où", "ce", "cet", "cette", "ces", "mais", "ou", "ni", "ne", "pas",
        "plus", "moins", "comme", "si", "tout", "tous", "toute", "toutes", "on", "nous", "vous", "il", "ils", "elle", "elles",
        "je", "tu", "me", "te", "se", "en", "y", "l", "d", "m", "s", "t", "n", "j", "c", "qu", "ai", "as", "a", "avons", "avez", "ont"
    };

    public GraphProcessingService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<GraphProcessingService> logger)
    {
        _serviceProvider = serviceProvider;
        _options = configuration.GetSection("GraphProcessing").Get<GraphProcessingOptions>()
                  ?? new GraphProcessingOptions();
        _logger = logger;
    }

    public async Task ComputeGraphAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LibraryContext>();
        var globalWatch = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation("=== DÉBUT DU TRAITEMENT DU GRAPHE ===");

            // 1. Chargement
            var stepWatch = Stopwatch.StartNew();
            var books = await LoadBooksAsync(context);
            stepWatch.Stop();
            _logger.LogInformation($"[METRIC] Chargement DB : {books.Count} livres en {stepWatch.ElapsedMilliseconds}ms");

            if (books.Count == 0) return;

            // 2. Tokenisation & Nettoyage
            stepWatch.Restart();
            var bookSets = BuildBookSets(books);
            stepWatch.Stop();
            _logger.LogInformation($"[METRIC] Tokenisation/Nettoyage : {stepWatch.ElapsedMilliseconds}ms");

            // 3. Index Inversé
            stepWatch.Restart();
            var invertedIndex = BuildAndCleanInvertedIndex(bookSets, books.Count);
            stepWatch.Stop();
            _logger.LogInformation($"[METRIC] Construction Index Inversé : {stepWatch.ElapsedMilliseconds}ms ({invertedIndex.Count} mots)");

            // 4. Calcul Jaccard 
            stepWatch.Restart();
            var (relations, graphAdj) = await Task.Run(() =>
                ComputeRelationsParallel(books, bookSets, invertedIndex));
            stepWatch.Stop();
            _logger.LogInformation($"[METRIC] Calcul Jaccard : {relations.Count} arêtes trouvées en {stepWatch.ElapsedMilliseconds}ms");

            // 5. Sauvegarde Relations
            stepWatch.Restart();
            await ClearExistingRelationsAsync(context);
            await OptimizedBulkInsertPostgresAsync(context, relations);
            stepWatch.Stop();
            _logger.LogInformation($"[METRIC] Persistance PostgreSQL (Binary Copy) : {stepWatch.ElapsedMilliseconds}ms");

            // 6. PageRank
            stepWatch.Restart();
            var scores = await Task.Run(() =>
                ComputePageRankOptimized(graphAdj, books.Select(b => b.Id).ToList()));
            stepWatch.Stop();
            _logger.LogInformation($"[METRIC] Calcul PageRank ({_options.PageRankIterations} iters) : {stepWatch.ElapsedMilliseconds}ms");

            // 7. Mise à jour Scores
            stepWatch.Restart();
            await UpdateBookScoresAsync(context, books, scores);
            stepWatch.Stop();
            _logger.LogInformation($"[METRIC] Mise à jour scores PageRank : {stepWatch.ElapsedMilliseconds}ms");

            globalWatch.Stop();
            _logger.LogInformation($"=== TRAITEMENT TERMINÉ en {globalWatch.Elapsed.TotalSeconds:F2}s ===");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur critique lors du traitement du graphe");
            throw;
        }
    }

    private async Task ClearExistingRelationsAsync(LibraryContext context)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"BookRelations\" RESTART IDENTITY;");
        }
        catch
        {
            await context.Database.ExecuteSqlRawAsync("DELETE FROM \"BookRelations\";");
        }
        _logger.LogDebug($"[INTERNAL] Truncate effectué en {sw.ElapsedMilliseconds}ms");
    }

    private async Task OptimizedBulkInsertPostgresAsync(LibraryContext context, List<BookRelation> relations)
    {
        if (relations.Count == 0) return;

        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();

        using (var writer = await connection.BeginBinaryImportAsync(
            "COPY \"BookRelations\" (\"SourceBookId\", \"TargetBookId\", \"Weight\") FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var rel in relations)
            {
                await writer.StartRowAsync();
                await writer.WriteAsync(rel.SourceBookId, NpgsqlTypes.NpgsqlDbType.Integer);
                await writer.WriteAsync(rel.TargetBookId, NpgsqlTypes.NpgsqlDbType.Integer);
                await writer.WriteAsync((double)rel.Weight, NpgsqlTypes.NpgsqlDbType.Double);
            }
            await writer.CompleteAsync();
        }
    }

    private async Task UpdateBookScoresAsync(LibraryContext context, List<BookInfo> books, Dictionary<int, double> scores)
    {
        context.ChangeTracker.Clear();
        foreach (var book in books)
        {
            if (scores.TryGetValue(book.Id, out var score))
            {
                var bookStub = new Book { Id = book.Id, PageRankScore = score };
                context.Books.Attach(bookStub);
                context.Entry(bookStub).Property(b => b.PageRankScore).IsModified = true;
            }
        }
        await context.SaveChangesAsync();
    }

    private async Task<List<BookInfo>> LoadBooksAsync(LibraryContext context)
    {
        return await context.Books
            .AsNoTracking()
            .Select(b => new BookInfo { Id = b.Id, IndexingTable = b.IndexingTable })
            .ToListAsync();
    }

    private Dictionary<int, HashSet<string>> BuildBookSets(List<BookInfo> books)
    {
        var bookSets = new Dictionary<int, HashSet<string>>(books.Count);
        foreach (var book in books)
        {
            var text = book.IndexingTable.ToLowerInvariant();
            var words = Regex.Matches(text, @"\b[a-z]+\b")
                .Select(m => m.Value)
                .Where(w => w.Length >= 3 && w.Length <= 20 && !StopWords.Contains(w) && !int.TryParse(w, out _))
                .Distinct()
                .Take(_options.MaxTermsPerBook)
                .ToHashSet(StringComparer.Ordinal);
            bookSets[book.Id] = words;
        }
        return bookSets;
    }

    private Dictionary<string, List<int>> BuildAndCleanInvertedIndex(Dictionary<int, HashSet<string>> bookSets, int totalBooks)
    {
        var invertedIndex = new Dictionary<string, List<int>>(Math.Max(10000, bookSets.Count * 10), StringComparer.Ordinal);

        foreach (var (bookId, words) in bookSets)
        {
            foreach (var word in words)
            {
                if (!invertedIndex.TryGetValue(word, out var bookList))
                {
                    bookList = new List<int>();
                    invertedIndex[word] = bookList;
                }
                bookList.Add(bookId);
            }
        }

        var minOccurrence = Math.Max(2, _options.MinWordOccurrence);
        var maxOccurrence = (int)(totalBooks * _options.MaxWordFrequency);

        var wordsToRemove = invertedIndex
            .Where(kvp => kvp.Value.Count < minOccurrence || kvp.Value.Count > maxOccurrence)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var word in wordsToRemove) invertedIndex.Remove(word);

        return invertedIndex;
    }

    private (List<BookRelation> relations, ConcurrentDictionary<int, ConcurrentBag<int>> graph)
    ComputeRelationsParallel(List<BookInfo> books, Dictionary<int, HashSet<string>> bookSets, Dictionary<string, List<int>> invertedIndex)
    {
        var relationsBag = new ConcurrentBag<BookRelation>();
        var graphAdj = new ConcurrentDictionary<int, ConcurrentBag<int>>();
        var allBookIds = books.Select(b => b.Id).ToList();

        foreach (var id in allBookIds) graphAdj[id] = new ConcurrentBag<int>();

        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount - 1 };

        Parallel.ForEach(allBookIds, parallelOptions, sourceId =>
        {
            var sourceWords = bookSets[sourceId];
            var candidates = new Dictionary<int, int>();

            foreach (var word in sourceWords)
            {
                if (invertedIndex.TryGetValue(word, out var sharedBooks))
                {
                    foreach (var targetId in sharedBooks)
                    {
                        if (targetId > sourceId)
                        {
                            candidates[targetId] = candidates.GetValueOrDefault(targetId) + 1;
                        }
                    }
                }
            }

            foreach (var kvp in candidates)
            {
                var targetId = kvp.Key;
                var intersection = kvp.Value;
                var targetWords = bookSets[targetId];
                var union = sourceWords.Count + targetWords.Count - intersection;

                if (union == 0) continue;
                double jaccard = (double)intersection / union;

                if (jaccard > _options.JaccardThreshold)
                {
                    var rel1 = new BookRelation { SourceBookId = sourceId, TargetBookId = targetId, Weight = jaccard };
                    var rel2 = new BookRelation { SourceBookId = targetId, TargetBookId = sourceId, Weight = jaccard };
                    relationsBag.Add(rel1);
                    relationsBag.Add(rel2);
                    graphAdj[sourceId].Add(targetId);
                    graphAdj[targetId].Add(sourceId);
                }
            }
        });

        return (relationsBag.ToList(), graphAdj);
    }

    private Dictionary<int, double> ComputePageRankOptimized(ConcurrentDictionary<int, ConcurrentBag<int>> graph, List<int> allIds)
    {
        var idToIndex = allIds.Select((id, idx) => (id, idx)).ToDictionary(x => x.id, x => x.idx);
        var indexToId = allIds.ToArray();
        int n = allIds.Count;

        double[] currentRanks = new double[n];
        double[] nextRanks = new double[n];
        Array.Fill(currentRanks, 1.0 / n);

        int[] degrees = new int[n];
        foreach (var (nodeId, neighbors) in graph) degrees[idToIndex[nodeId]] = neighbors.Count;

        double dampingFactor = _options.PageRankDampingFactor;
        double teleportation = (1.0 - dampingFactor) / n;

        for (int iter = 0; iter < _options.PageRankIterations; iter++)
        {
            Array.Fill(nextRanks, teleportation);

            foreach (var (nodeId, neighbors) in graph)
            {
                int nodeIdx = idToIndex[nodeId];
                if (degrees[nodeIdx] == 0) continue;

                double contribution = dampingFactor * (currentRanks[nodeIdx] / degrees[nodeIdx]);
                foreach (var neighborId in neighbors)
                {
                    nextRanks[idToIndex[neighborId]] += contribution;
                }
            }
            (currentRanks, nextRanks) = (nextRanks, currentRanks);
        }

        var result = new Dictionary<int, double>(n);
        for (int i = 0; i < n; i++) result[indexToId[i]] = currentRanks[i];
        return result;
    }
}

public class BookInfo
{
    public int Id { get; set; }
    public string IndexingTable { get; set; } = string.Empty;
}

public class GraphProcessingOptions
{
    public double JaccardThreshold { get; set; } = 0.05;
    public double PageRankDampingFactor { get; set; } = 0.85;
    public int PageRankIterations { get; set; } = 20;
    public int MaxTermsPerBook { get; set; } = 5000;
    public int BatchSize { get; set; } = 5000;
    public int MinWordOccurrence { get; set; } = 2;
    public double MaxWordFrequency { get; set; } = 0.6;
}