using Library.Api.Models;
using System.Text.RegularExpressions;
using System.Diagnostics; // Import nécessaire pour Stopwatch

namespace Library.Api.Data;

public static class DataInitializer
{
    public static async Task SeedAsync(LibraryContext context, string booksFolderPath)
    {
        if (context.Books.Any()) return;

        var files = Directory.GetFiles(booksFolderPath, "*.txt");
        var booksToInsert = new List<Book>();

        Console.WriteLine($"--- [START] Importation de {files.Length} livres ---");

        // Chronomètre global pour la section "Performances de l'ingestion"
        var globalWatch = Stopwatch.StartNew();
        long totalRawSize = 0;
        long totalCleanSize = 0;

        foreach (var file in files)
        {
            var fileInfo = new FileInfo(file);
            totalRawSize += fileInfo.Length;

            // Chronomètre par livre pour calculer la moyenne et l'écart-type
            var bookWatch = Stopwatch.StartNew();

            var rawContent = await File.ReadAllTextAsync(file);

            // 1. Nettoyage
            var cleanContent = ExtractCleanContent(rawContent);
            totalCleanSize += System.Text.Encoding.UTF8.GetByteCount(cleanContent);

            // 2. Extraction titre
            var displayTitle = ExtractTitle(rawContent) ?? Path.GetFileNameWithoutExtension(file);

            // 3. Indexation
            var words = cleanContent.ToLower()
                .Split(new[] { ' ', '.', ',', ';', '!', '?', '\r', '\n', '(', ')', '[', ']', '\"' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 3)
                .Distinct()
                .ToList();

            var book = new Book
            {
                Title = displayTitle,
                Content = cleanContent,
                IndexingTable = string.Join(" ", words),
                PageRankScore = 1.0
            };

            booksToInsert.Add(book);
            bookWatch.Stop();

            // Log de métrique individuelle (Utile pour tes futurs graphiques)
            // Console.WriteLine($"[METRIC_UNIT] Book: {displayTitle}, Time: {bookWatch.ElapsedMilliseconds}ms");

            if (booksToInsert.Count >= 50)
            {
                var dbWatch = Stopwatch.StartNew();
                await context.Books.AddRangeAsync(booksToInsert);
                await context.SaveChangesAsync();
                dbWatch.Stop();

                Console.WriteLine($"[BATCH] 50 livres insérés en {dbWatch.ElapsedMilliseconds}ms. Dernier : {displayTitle}");
                booksToInsert.Clear();
            }
        }

        if (booksToInsert.Any())
        {
            await context.Books.AddRangeAsync(booksToInsert);
            await context.SaveChangesAsync();
        }

        globalWatch.Stop();

        // --- SECTION MÉTRIQUES POUR LE RAPPORT ---
        Console.WriteLine("\n--- [RESULTATS MÉTRIQUES POUR LE RAPPORT] ---");
        Console.WriteLine($"[METRIC] Temps total d'ingestion : {globalWatch.Elapsed.TotalSeconds:F2} secondes");
        Console.WriteLine($"[METRIC] Nombre total de livres : {files.Length}");
        Console.WriteLine($"[METRIC] Taille brute totale : {totalRawSize / 1024.0 / 1024.0:F2} Mo");
        Console.WriteLine($"[METRIC] Taille nettoyée totale : {totalCleanSize / 1024.0 / 1024.0:F2} Mo");
        Console.WriteLine($"[METRIC] Gain de stockage (Nettoyage) : {((1 - (double)totalCleanSize / totalRawSize) * 100):F1}%");
        Console.WriteLine($"[METRIC] Temps moyen par livre : {(double)globalWatch.ElapsedMilliseconds / files.Length:F2} ms/livre");
        Console.WriteLine("----------------------------------------------\n");
    }

    private static string ExtractCleanContent(string rawText)
    {
        var startMatch = Regex.Match(rawText, @"\*\*\* START OF (THE|THIS) PROJECT GUTENBERG EBOOK .* \*\*\*");
        var endMatch = Regex.Match(rawText, @"\*\*\* END OF (THE|THIS) PROJECT GUTENBERG EBOOK .* \*\*\*");

        int startIndex = startMatch.Success ? startMatch.Index + startMatch.Length : 0;
        int endIndex = endMatch.Success ? endMatch.Index : rawText.Length;

        if (endIndex <= startIndex) return rawText;

        return rawText.Substring(startIndex, endIndex - startIndex).Trim();
    }

    private static string? ExtractTitle(string rawText)
    {
        var match = Regex.Match(rawText, @"Title:\s*(.+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
}