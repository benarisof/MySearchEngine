using Library.Api.Data;
using Library.Api.Services;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics; // Import indispensable pour les métriques

var builder = WebApplication.CreateBuilder(args);

// --- 1. CONFIGURATION DES SERVICES ---
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddScoped<SearchService>();
builder.Services.AddScoped<BookService>();
builder.Services.AddScoped<GraphProcessingService>();
builder.Services.AddScoped<GutenbergDownloader>();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<LibraryContext>(options => options.UseNpgsql(connectionString));

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngular", policy =>
    {
        policy.WithOrigins("http://172.16.8.72", "http://localhost")
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

// --- 2. PIPELINE HTTP ---
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowAngular");
app.UseAuthorization();
app.MapControllers();

// --- 3. LOGIQUE D'INITIALISATION INSTRUMENTÉE ---
_ = Task.Run(async () =>
{
    using var scope = app.Services.CreateScope();
    var services = scope.ServiceProvider;
    var context = services.GetRequiredService<LibraryContext>();
    var downloader = services.GetRequiredService<GutenbergDownloader>();
    var graphService = services.GetRequiredService<GraphProcessingService>();

    Console.WriteLine("=== [METRIC_START] DÉMARRAGE DU PIPELINE COMPLET ===");
    var totalPipelineWatch = Stopwatch.StartNew();

    try
    {
        // ÉTAPE A : Attente PostgreSQL
        bool dbReady = false;
        int retries = 0;
        while (!dbReady && retries < 20)
        {
            try { context.Database.EnsureCreated(); dbReady = true; }
            catch
            {
                retries++;
                Console.WriteLine($"[Init] Attente DB... ({retries}/20)");
                await Task.Delay(5000);
            }
        }

        // ÉTAPE B : Téléchargement 
        var booksPath = Path.Combine(Directory.GetCurrentDirectory(), "data_books");
        if (!Directory.Exists(booksPath)) Directory.CreateDirectory(booksPath);

        var downloadWatch = Stopwatch.StartNew();
        await downloader.EnsureBooksExistAsync(booksPath, 1664);
        downloadWatch.Stop();
        Console.WriteLine($"[METRIC] Phase Téléchargement : {downloadWatch.Elapsed.TotalSeconds:F2}s");

        // ÉTAPE C : Seed (Mesurer l'ingestion disque -> DB)
        if (!await context.Books.AnyAsync())
        {
            var seedWatch = Stopwatch.StartNew();
            await DataInitializer.SeedAsync(context, booksPath);
            seedWatch.Stop();
            Console.WriteLine($"[METRIC] Phase Seed/Nettoyage : {seedWatch.Elapsed.TotalSeconds:F2}s");
        }

        // ÉTAPE D : Calcul du Graphe 
        if (!await context.BookRelations.AnyAsync())
        {
            var graphWatch = Stopwatch.StartNew();
            await graphService.ComputeGraphAsync();
            graphWatch.Stop();
            Console.WriteLine($"[METRIC] Phase Graphe (Jaccard + PageRank) : {graphWatch.Elapsed.TotalSeconds:F2}s");
        }

        totalPipelineWatch.Stop();
        Console.WriteLine("=====================================================");
        Console.WriteLine($"[METRIC_TOTAL] Système prêt en {totalPipelineWatch.Elapsed.TotalMinutes:F2} minutes");
        Console.WriteLine("=====================================================");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERREUR CRITIQUE] : {ex.Message}");
    }
});

app.Run();