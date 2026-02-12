using Library.Api.Data;
using Library.Api.DTOs;
using Microsoft.EntityFrameworkCore;

namespace Library.Api.Services
{
    public class BookService
    {
        private readonly LibraryContext _context;

        public BookService(LibraryContext context)
        {
            _context = context;
        }
        public async Task<BookDetailDto?> GetBook(int id)
        {
            // On cherche le livre par son ID
            var book = await _context.Books
                .AsNoTracking() 
                .Where(b => b.Id == id)
                .Select(b => new BookDetailDto
                {
                    Id = b.Id,
                    Title = b.Title,
                    Content = b.Content,
                })
                .FirstOrDefaultAsync();
            return book;
        }
    }
}
