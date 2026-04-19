using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

const string UrlsFile = "\\Users\\arnau\\Languages\\CSharp\\LighthouseBatch\\Urls.txt";
const string OutputCsv = "\\Users\\arnau\\Languages\\CSharp\\LighthouseBatch\\results.csv";
const string Categories = "performance,accessibility,best-practices,seo";
const string ChromeFlags = "--headless=new --no-sandbox --disable-dev-shm-usage";
const string tempDir = "Temp";
const int TimeoutSeconds = 600;

if (!File.Exists(UrlsFile))
{
    Console.Error.WriteLine($"Fichier introuvable: {UrlsFile}");
    Environment.Exit(1);
}

var urls = File.ReadAllLines(UrlsFile)
    .Select(x => x.Trim())
    .Where(x => !string.IsNullOrWhiteSpace(x))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToList();

if (urls.Count == 0)
{
    Console.Error.WriteLine("No URL found in file urls.txt");
    Environment.Exit(1);
}

var results = new List<ResultRow>();

for (int i = 0; i < urls.Count; i++)
{
    var url = urls[i];
    Console.WriteLine($"[{i + 1}/{urls.Count}] {url}");
   
    var result = await RunLighthouseAsync(url);
    results.Add(result);
}

WriteCsv(OutputCsv, results);
Console.WriteLine($"Finish. Results written in {OutputCsv}");

static async Task<ResultRow> RunLighthouseAsync(string url)
{

    // 1. Extraire le nom de domaine
    string fileName = ExtractDomain(url);

    // 2 Nettoyer pour Windows (sécurité)
    fileName = SanitizeFileName(fileName);

    // 3. Construire le chemin complet
    string outputFile = Path.Combine("./Json/", fileName + ".json");    
    
    var args =
        $"npx lighthouse {url} " +
        $"--only-categories={Quote(Categories)} " +
        $"--output=json " +
        $"--output-path={Quote(outputFile)} " +
        $"--quiet " +
        $"--chrome-flags={Quote(ChromeFlags)}";
    
    using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {args}",
                    WorkingDirectory = Directory.GetCurrentDirectory(),
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }

            };
    
    process.StartInfo.Environment["TEMP"] = tempDir;
    process.StartInfo.Environment["TMP"] = tempDir;

    try
    {   
        Console.WriteLine($"{url} Process Launch");
        if (!process.Start())
        {
            return Error(url, "Impossible to start Lighthouse.");
        }
    
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));
        await process.WaitForExitAsync(cts.Token);

        Console.WriteLine($"{url} Timeout set");

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync();
            throw new Exception($"Lighthouse failed for url {url} with code {process.ExitCode}. Detail: {TrimError(error)}"); 
        }
        
        Console.WriteLine($"{url} Process prepare le json ");
        
        var jsonDoc = JsonDocument.Parse(await File.ReadAllTextAsync(outputFile));
        var rootDoc = jsonDoc.RootElement;

        File.Delete(outputFile); // nettoyage du fichier temporaire
        
        Console.WriteLine($"{url} Json collected and parsed");

        return new ResultRow
        {
            Url = url,
            Status = "OK",
            Performance = To100(ReadScore(rootDoc, "performance")),
            Accessibility = To100(ReadScore(rootDoc, "accessibility")),
            BestPractices = To100(ReadScore(rootDoc, "best-practices")),
            Seo = To100(ReadScore(rootDoc, "seo")),
            Error = ""
        };
    }
    catch (OperationCanceledException)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // ignore
        }

        return Error(url, $"Timeout after {TimeoutSeconds}s");
    }
    catch (Exception ex)
    {
        return Error(url, ex.Message);
    }
}

static double? ReadScore(JsonElement root, string categoryName)
{
    if (root.TryGetProperty("categories", out var categories) &&
        categories.TryGetProperty(categoryName, out var category) &&
        category.TryGetProperty("score", out var score) &&
        score.ValueKind == JsonValueKind.Number)
    {
        return score.GetDouble();
    }

    return null;
}

static int? To100(double? score)
{
    if (!score.HasValue)
        return null;

    return (int)Math.Round(score.Value * 100, MidpointRounding.AwayFromZero);
}

static ResultRow Error(string url, string error) => new()
{
    Url = url,
    Status = "ERROR",
    Performance = null,
    Accessibility = null,
    BestPractices = null,
    Seo = null,
    Error = error
};

static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

static string TrimError(string input)
{
    if (string.IsNullOrWhiteSpace(input))
        return "";

    input = input.Replace(Environment.NewLine, " ").Trim();
    return input.Length <= 1000 ? input : input[..1000];
}

static void WriteCsv(string path, List<ResultRow> rows)
{
    using var writer = new StreamWriter(path, false, new UTF8Encoding(true));

    writer.WriteLine("url,status,performance,accessibility,best_practices,seo,error");

    foreach (var row in rows)
    {
        writer.WriteLine(string.Join(",",
            Csv(row.Url),
            Csv(row.Status),
            Csv(row.Performance?.ToString(CultureInfo.InvariantCulture) ?? ""),
            Csv(row.Accessibility?.ToString(CultureInfo.InvariantCulture) ?? ""),
            Csv(row.BestPractices?.ToString(CultureInfo.InvariantCulture) ?? ""),
            Csv(row.Seo?.ToString(CultureInfo.InvariantCulture) ?? ""),
            Csv(row.Error)
        ));
    }
}

static string Csv(string value)
{
    if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
        return $"\"{value.Replace("\"", "\"\"")}\"";

    return value;
}

static string ExtractDomain(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.Host; // ex: toto.fr
        }

        // fallback si URL mal formée
        return url
            .Replace("https://", "")
            .Replace("http://", "")
            .Split('/')[0];
    }

static string SanitizeFileName(string input)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            input = input.Replace(c, '_');
        }

        // optionnel : éviter espaces ou trucs bizarres
        return Regex.Replace(input, @"\s+", "_");
    }


sealed class ResultRow
{
    public string Url { get; set; } = "";
    public string Status { get; set; } = "";
    public int? Performance { get; set; }
    public int? Accessibility { get; set; }
    public int? BestPractices { get; set; }
    public int? Seo { get; set; }
    public string Error { get; set; } = "";
}

