namespace Library.Api.Services;

public class GutenbergDownloader
{
    private readonly HttpClient _httpClient;

    public GutenbergDownloader()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
    }

    public async Task EnsureBooksExistAsync(string folderPath, int targetCount)
    {
        var existingFiles = Directory.GetFiles(folderPath, "book_*.txt");
        int downloaded = existingFiles.Length;

        if (downloaded >= targetCount) return;

        int currentId = 10;
        if (existingFiles.Any())
        {
            currentId = existingFiles
                .Select(f => int.Parse(Path.GetFileNameWithoutExtension(f).Replace("book_", "")))
                .Max() + 1;
        }

        Console.WriteLine($"--- Reprise du téléchargement. Déjà : {downloaded}/{targetCount}. ID départ : {currentId} ---");

        while (downloaded < targetCount)
        {
            try
            {
                //url 
                string url = $"https://www.gutenberg.org/files/{currentId}/{currentId}-0.txt";
                // url de secours
                string altUrl = $"https://www.gutenberg.org/cache/epub/{currentId}/pg{currentId}.txt";

                var response = await _httpClient.GetAsync(altUrl);

                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync();

                    // comptage mots
                    int wordCount = content.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;

                    if (wordCount >= 10000)
                    {
                        string filePath = Path.Combine(folderPath, $"book_{currentId}.txt");
                        await File.WriteAllTextAsync(filePath, content);
                        downloaded++;
                        Console.WriteLine($"[OK] {downloaded}/{targetCount} | ID: {currentId} | Mots: {wordCount}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SKIP] Erreur ID {currentId}: {ex.Message.Substring(0, Math.Min(50, ex.Message.Length))}");
            }

            currentId++;

            if (currentId % 10 == 0) await Task.Delay(200);
            if (currentId > 50000) break;
        }
    }
}