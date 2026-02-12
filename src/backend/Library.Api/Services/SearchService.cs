using Library.Api.Data;
using Library.Api.DTOs;
using Library.Api.Models;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using System.Text.RegularExpressions;

namespace Library.Api.Services;

public class SearchService
{
    private readonly LibraryContext _context;

    public SearchService(LibraryContext context)
    {
        _context = context;
    }

    // --- RECHERCHE SIMPLE AVEC PRIORITÉ EXACTE ---
    public async Task<SearchResultDto> SimpleSearchAsync(string query)
    {
        var result = new SearchResultDto();
        if (string.IsNullOrWhiteSpace(query)) return result;

        string cleanQuery = query.ToLower().Trim();
        var terms = cleanQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return result;

        // 1. SQL : Filtrage rapide via l'IndexingTable uniquement
        var queryable = _context.Books.AsNoTracking();
        foreach (var term in terms)
        {
            queryable = queryable.Where(b => b.IndexingTable.Contains(term));
        }

        // On récupère une "shortlist" de candidats basés sur la popularité
        // On ne fait AUCUN calcul de texte lourd (Contains sur Content) en SQL ici
        var candidates = await queryable
            .OrderByDescending(b => b.PageRankScore)
            .Take(100) // On prend les 100 plus populaires qui contiennent les mots
            .ToListAsync();

        // 2. C# : Tri de précision en mémoire (très rapide sur 100 items)
        var sortedMatches = candidates
            .Select(b => new
            {
                Book = b,
                // Score de match exact calculé en RAM
                Level = terms.Length > 1
                    ? (b.Title.Contains(cleanQuery, StringComparison.OrdinalIgnoreCase) ? 2
                       : (b.Content.Contains(cleanQuery, StringComparison.OrdinalIgnoreCase) ? 1 : 0))
                    : 0
            })
            .OrderByDescending(x => x.Level)
            .ThenByDescending(x => x.Book.PageRankScore)
            .Take(20) // On garde les 20 meilleurs finaux
            .ToList();

        // 3. Transformation en DTO
        result.Matches = sortedMatches.Select(x =>
        {
            var b = x.Book;
            string snippetTerm = (x.Level > 0) ? cleanQuery : terms[0];

            return new BookSummaryDto
            {
                Id = b.Id,
                Title = b.Title,
                RelevanceScore = b.PageRankScore,
                Snippet = GetContextualSnippet(b.Content, snippetTerm)
            };
        }).ToList();

        // 4. Suggestions (inchangé)
        if (result.Matches.Any())
        {
            var topBookIds = result.Matches.Take(3).Select(m => m.Id).ToList();
            var suggestionsData = await _context.BookRelations
                .AsNoTracking()
                .Where(r => topBookIds.Contains(r.SourceBookId))
                .Where(r => !topBookIds.Contains(r.TargetBookId))
                .OrderByDescending(r => r.Weight)
                .Select(r => new { r.TargetBookId, r.TargetBook!.Title, r.Weight, SourceTitle = r.SourceBook!.Title })
                .ToListAsync();

            result.Suggestions = suggestionsData
                .GroupBy(x => x.TargetBookId)
                .Select(g => g.First())
                .Take(5)
                .Select(x => new BookSummaryDto
                {
                    Id = x.TargetBookId,
                    Title = x.Title,
                    RelevanceScore = x.Weight,
                    Snippet = $"Similaire à : {x.SourceTitle}"
                }).ToList();
        }

        return result;
    }

    // --- RECHERCHE AVANCÉE (REGEX) ---
    public async Task<SearchResultDto> AdvancedSearchAsync(string pattern)
    {
        var result = new SearchResultDto();
        var tempResults = new List<(BookSummaryDto Dto, int Level)>();
        Regex regex;

        try
        {
            // On compile la regex pour optimiser les performances sur le volume de données
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException) { throw; }

        // 1. SCAN DE LA BASE (Streaming)
        var booksStream = _context.Books
            .AsNoTracking()
            .Select(b => new { b.Id, b.Title, b.Content, b.PageRankScore })
            .AsAsyncEnumerable();

        await foreach (var book in booksStream)
        {
            try
            {
                int matchLevel = 0;

                // Priorité 2 : Match dans le Titre
                bool titleMatched = regex.IsMatch(book.Title);

                // Priorité 1 : Match dans le Contenu (on récupère le match pour le snippet)
                var contentMatch = regex.Match(book.Content);

                if (titleMatched) matchLevel = 2;
                else if (contentMatch.Success) matchLevel = 1;

                if (matchLevel > 0)
                {
                    tempResults.Add((new BookSummaryDto
                    {
                        Id = book.Id,
                        Title = book.Title,
                        RelevanceScore = book.PageRankScore,
                        Snippet = GetContextualSnippet(book.Content, contentMatch.Success ? contentMatch.Value : "")
                    }, matchLevel));
                }
            }
            catch (RegexMatchTimeoutException) { continue; }
        }

        // 2. TRI ET REMPLISSAGE DES MATCHES
        result.Matches = tempResults
            .OrderByDescending(r => r.Level)
            .ThenByDescending(r => r.Dto.RelevanceScore)
            .Take(20)
            .Select(r => r.Dto)
            .ToList();

        // 3. GÉNÉRATION DES SUGGESTIONS (Basées sur le graphe Jaccard)
        if (result.Matches.Any())
        {
            // On prend les 3 meilleurs IDs pour trouver des voisins pertinents
            var topBookIds = result.Matches.Take(3).Select(m => m.Id).ToList();

            var suggestionsData = await _context.BookRelations
                .AsNoTracking()
                .Where(r => topBookIds.Contains(r.SourceBookId))
                .Where(r => !topBookIds.Contains(r.TargetBookId)) // Éviter de suggérer ce qu'on a déjà trouvé
                .OrderByDescending(r => r.Weight)
                .Select(r => new
                {
                    r.TargetBookId,
                    r.TargetBook!.Title,
                    r.Weight,
                    SourceTitle = r.SourceBook!.Title
                })
                .ToListAsync();

            result.Suggestions = suggestionsData
                .GroupBy(x => x.TargetBookId) // Éviter les doublons si plusieurs sources pointent vers la même cible
                .Select(g => g.First())
                .Take(5)
                .Select(x => new BookSummaryDto
                {
                    Id = x.TargetBookId,
                    Title = x.Title,
                    RelevanceScore = x.Weight,
                    Snippet = $"Parce que vous avez trouvé : {x.SourceTitle}"
                })
                .ToList();
        }

        return result;
    }

    // --- LOGIQUE DE SNIPPET AMÉLIORÉE ---
    private string GetContextualSnippet(string content, string term)
    {
        if (string.IsNullOrEmpty(content)) return "";
        if (string.IsNullOrEmpty(term)) return content.Substring(0, Math.Min(100, content.Length)) + "...";

        int index = content.IndexOf(term, StringComparison.OrdinalIgnoreCase);

        // Si le terme exact n'est pas trouvé dans Content (cas rare de désynchro IndexingTable/Content)
        // On se rabat sur le premier mot
        if (index == -1)
        {
            string firstWord = term.Split(' ')[0];
            index = content.IndexOf(firstWord, StringComparison.OrdinalIgnoreCase);
        }

        if (index == -1) return content.Substring(0, Math.Min(100, content.Length)) + "...";

        int start = Math.Max(0, index - 60);
        int length = Math.Min(120, content.Length - start);

        string snippet = content.Substring(start, length).Replace("\n", " ").Replace("\r", "");
        return (start > 0 ? "..." : "") + snippet + (start + length < content.Length ? "..." : "");
    }
}