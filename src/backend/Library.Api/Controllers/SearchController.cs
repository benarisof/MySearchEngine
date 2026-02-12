using Library.Api.DTOs;
using Library.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileSystemGlobbing.Internal;

namespace Library.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SearchController : ControllerBase
{
    private readonly SearchService _searchService;
    private readonly BookService _bookService;

    public SearchController(SearchService searchService, BookService bookService)
    {
        _searchService = searchService;
        _bookService = bookService;
    }

    [HttpGet("simple")]
    public async Task<IActionResult> Search([FromQuery] string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return BadRequest("La requête ne peut pas être vide.");

        var results = await _searchService.SimpleSearchAsync(query);
        return Ok(results);
    }

    [HttpGet("advanced")]
    public async Task<IActionResult> AdvancedSearch([FromQuery] string pattern)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return BadRequest("Le pattern ne peut pas être vide.");

            var results = await _searchService.AdvancedSearchAsync(pattern);
            return Ok(results);
        }
        catch (ArgumentException)
        {
            return BadRequest("Expression régulière invalide.");
        }
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetBook(int id)
    {
        try
        {   
            BookDetailDto result = await _bookService.GetBook(id);
            if (result == null)
                return NotFound();
            return Ok(result);
        }
        catch (ArgumentException)
        {
            return BadRequest("Expression régulière invalide.");
        }
    }
}