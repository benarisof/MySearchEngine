using Library.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Library.Api.Data;

public class LibraryContext : DbContext
{
    public LibraryContext(DbContextOptions<LibraryContext> options) : base(options) { }

    public DbSet<Book> Books => Set<Book>();

    public DbSet<BookRelation> BookRelations => Set<BookRelation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BookRelation>()
            .HasOne(br => br.SourceBook)
            .WithMany()
            .HasForeignKey(br => br.SourceBookId);
    }
}