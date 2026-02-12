# --- CONFIGURATION ---
# BaseUri : Nom du contrôleur (Search)
# Port 5087 : Mappage Docker (Host:5087 -> Container:8080)
$baseUri = "http://localhost:5087/api/Search" 
$count = 100

function Run-Benchmark($name, $endpoint, $paramName, $queryValue) {
    Write-Host "`n--- Test : $name ($count requêtes) ---" -ForegroundColor Cyan
    $times = @()
    $successCount = 0
    
    # CONSTRUCTION DE L'URL (Format: http://localhost:5087/api/Search/simple?query=science)
    # On utilise `? pour s'assurer que PowerShell traite le point d'interrogation comme du texte
    $fullUrl = "$baseUri/$endpoint`?$paramName=$([Uri]::EscapeDataString($queryValue))"

    for ($i = 1; $i -le $count; $i++) {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        try {
            # Appel API
            $response = Invoke-WebRequest -Uri $fullUrl -Method Get -UseBasicParsing -ErrorAction Stop
            $sw.Stop()
            
            if ($response.StatusCode -eq 200) {
                $successCount++
                $times += $sw.ElapsedMilliseconds
            }
        }
        catch {
            $sw.Stop()
            if ($i -eq 1) { 
                Write-Host "[ERREUR DEBUG] URL testée : $fullUrl" -ForegroundColor Yellow
                if ($_.Exception.Response) {
                    Write-Host "[ERREUR DEBUG] Status API : $($_.Exception.Response.StatusCode)" -ForegroundColor Red
                } else {
                    Write-Host "[ERREUR DEBUG] Message : $($_.Exception.Message)" -ForegroundColor Red
                }
            }
        }

        if ($i % 25 -eq 0) { Write-Host "Progression : $i/$count..." }
    }

    # Calcul des statistiques
    $avg = if ($times.Count -gt 0) { ($times | Measure-Object -Average).Average } else { 0 }
    $max = if ($times.Count -gt 0) { ($times | Measure-Object -Maximum).Maximum } else { 0 }
    
    return [PSCustomObject]@{ 
        Type       = $name; 
        Succes     = "$successCount/$count";
        Moyenne_ms = [Math]::Round($avg, 2); 
        Max_ms     = $max 
    }
}

# --- EXÉCUTION DES TESTS ---

# 1. Test Recherche Simple (Route [HttpGet("simple")] -> param 'query')
$res1 = Run-Benchmark "Recherche Simple" "simple" "query" "science"

# 2. Test Regex Basique (Route [HttpGet("advanced")] -> param 'pattern')
$res2 = Run-Benchmark "Regex Basique" "advanced" "pattern" "^The"

# 3. Test Regex Complexe (Route [HttpGet("advanced")] -> param 'pattern')
$res3 = Run-Benchmark "Regex Complexe" "advanced" "pattern" "\b[a-zA-Z]{5}ion\b"

# --- AFFICHAGE DES RÉSULTATS ---

Write-Host "`n========================================" -ForegroundColor Green
Write-Host "     RÉSULTATS DU BENCHMARK DOCKER" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green

$results = @($res1, $res2, $res3)
$results | Format-Table -AutoSize

# --- EXPORTATION ---
$outputPath = Join-Path $PSScriptRoot "stats_performance.csv"
if ([string]::IsNullOrWhiteSpace($PSScriptRoot)) { $outputPath = ".\stats_performance.csv" }

try {
    $results | Export-Csv -Path $outputPath -NoTypeInformation -Encoding utf8 -Force
    Write-Host "`n[SUCCÈS] CSV généré : $outputPath" -ForegroundColor Cyan
}
catch {
    Write-Host "`n[ERREUR] Impossible de créer le fichier CSV." -ForegroundColor Red
}

Write-Host "`n--- Fin des tests ---"
Read-Host -Prompt "Appuyez sur Entrée pour fermer"