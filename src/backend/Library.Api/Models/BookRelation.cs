
namespace Library.Api.Models;

public class BookRelation
{
    public int Id { get; set; }

    // Livre source
    public int SourceBookId { get; set; }
    public Book? SourceBook { get; set; }

    // Livre cible (Voisin)
    public int TargetBookId { get; set; }
    public Book? TargetBook { get; set; }

    // Poids de l'arête (Score Jaccard)
    public double Weight { get; set; }
}