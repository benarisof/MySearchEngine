using System.ComponentModel.DataAnnotations;

namespace Library.Api.DTOs;

public class BookSummaryDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public double RelevanceScore { get; set; } // PageRank ou Jaccard
    public string Snippet { get; set; } = string.Empty; // Extrait du texte
}

public class BookDetailDto
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

}

public class SearchResultDto
{
    // Les résultats directs de la recherche
    public List<BookSummaryDto> Matches { get; set; } = new();

    // Les suggestions implicites 
    public List<BookSummaryDto> Suggestions { get; set; } = new();
}