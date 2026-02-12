using System.ComponentModel.DataAnnotations;

namespace Library.Api.Models;

public class Book
{
    public int Id { get; set; }

    [Required]
    public string Title { get; set; } = string.Empty;

    [Required]
    public string Content { get; set; } = string.Empty; 


    // on stocke les mots-clefs uniques ici
    public string IndexingTable { get; set; } = string.Empty;

    // Métadonnées pour la pertinence 
    public double PageRankScore { get; set; } = 0.0;
}