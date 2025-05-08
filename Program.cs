using System.Text;
using System.Text.RegularExpressions;
using LibGit2Sharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using YoutubeTranscriptApi;
using Path = System.IO.Path;

namespace nyingest;

class Program
{
    public static bool IsMatch(string input, string pattern)
    {
        string regexPattern = "^" + Regex.Escape(pattern)
                .Replace("\\*", ".*") // Replace * with .*
                .Replace("\\?", ".") // Replace ? with .
                .Replace("\\/", "/") // Allow / to match /
            ;
        // + ".*$"; // Allow for additional characters after the pattern

        return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase); // Add IgnoreCase option
    }

    public static async Task<string> GetTranscriptAsync(string videoId)
    {
        try {
            var transcriptapi = new YouTubeTranscriptApi();

            //TODO BUG `An item with the same key has already been added. Key: en`
            var transcriptItems = transcriptapi.GetTranscript(videoId);
            StringBuilder sb = new();

            foreach (var item in transcriptItems) {
                sb.Append(item.Text);
                sb.Append(" ");
            }

            return sb.ToString();
        }
        catch (Exception ex) {
            string msg = $"Error retrieving transcript: {ex.ToString()} | {ex.Message}";
            Console.Error.WriteLine(msg);
            return "";
        }
    }

    public static async Task<string> GetGitRepoAsync(string giturl, string matchWildcards, string negativeWildcards,
        long max_filesize_B = 0)
    {
        Directory.CreateDirectory("cache");

        string reponame = giturl.Split('/')[^1].Replace(".git", "");

        string? repo = $"cache/{reponame}";
        if (!Directory.Exists($"cache/{reponame}")) {
            repo = Repository.Clone(giturl, $"cache/{reponame}");
            if (repo == null) {
                return $"Failure to clone repository at `{giturl}`";
            }
        }

        List<string> files = Directory.EnumerateFiles(repo, "", SearchOption.AllDirectories).ToList();
        var matches = matchWildcards.Split(',');
        foreach (var match in matches) {
            files = files.Where(x => { return IsMatch(x, match); }).ToList();
        }

        // var negativeMatches = negativeWildcards.Split(",");
        // foreach (var negative in negativeMatches) {
        //     files = files.Where(x => { return !IsMatch(x, negative); }).ToList();
        // }

        files = files.Distinct().ToList();

        StringBuilder contents = new();
        foreach (var filename in files) {
            string file = filename.Replace("\\", "/");

            if (file.StartsWith(".git")) {
                continue;
            }

            var inf = new FileInfo(file);
            if (max_filesize_B >= 1 && inf.Length > max_filesize_B) {
                continue;
            }

            if (inf.Length == 0) {
                continue;
            }

            contents.AppendFormat("---- {0} ---- \n", file);

            using (StreamReader reader = File.OpenText(file)) {
                string? line;
                while ((line = reader.ReadLine()) != null) {
                    // Only append non empty lines
                    if (line.Trim() != line) {
                        contents.AppendLine(line);
                    }
                }
            }

            contents.AppendFormat("---- end of {0} ----\n\n", Path.GetFileName(file));
        }

        return contents.ToString();
    }

    public static async Task<string> GetPdfAsync(string url)
    {
        StringBuilder sb = new();

        if (url.Contains("http")) {
            using (HttpClient client = new HttpClient()) {
                byte[] bytes = client.GetByteArrayAsync(url).GetAwaiter().GetResult();

                using (PdfDocument document = PdfDocument.Open(bytes)) {
                    for (int i = 1; i <= document.NumberOfPages; i++) {
                        string text = document.GetPage(i).Text;
                        sb.AppendLine(text);
                    }
                }
            }
        }
        else if (File.Exists(url)) {
            using (PdfDocument document = PdfDocument.Open(File.ReadAllBytes(url))) {
                for (int i = 1; i <= document.NumberOfPages; i++) {
                    string text = document.GetPage(i).Text;
                    sb.AppendLine(text);
                }
            }
        }

        return sb.ToString();
    }

    enum WebsiteSource
    {
        Youtube,
        Git,
        Pdf,
        Other
    }

    static void Main(string[] args)
    {
        // string url = @"https://youtu.be/VnBYQrPacqg?si=FU1dfL71RD8tWqo0";
        // string url = @"https://www.youtube.com/watch?v=ahOfNgvQ93Q";
        // string url = @"http://cslibrary.stanford.edu/101/EssentialC.pdf";
        // string url = @"https://github.com/xing1357/SimpleOS";
        // string url = @"https://gitlab.com/enderice2/Fennix";
        string url = "";

        if (args.Length > 0) {
            url = args[0];
        }

        string id = "";

        WebsiteSource source = WebsiteSource.Other;
        string low = url.ToLower();

        // Youtube 
        {
            if (low.Contains("youtube.com")) {
                id = url.Split("?v=")[1].Split("?")[0];
                source = WebsiteSource.Youtube;
            }

            if (low.Contains("youtu.be")) {
                id = url.Split("youtu.be/")[1].Split("?")[0];
                source = WebsiteSource.Youtube;
            }
        }

        // Github
        {
            if (low.Contains("github.com") || low.Contains("gitlab.com") || low.EndsWith(".git")) {
                if (!low.EndsWith(".git")) {
                    url += ".git";
                }

                source = WebsiteSource.Git;
            }
        }

        // PDF
        {
            if (low.EndsWith(".pdf")) {
                source = WebsiteSource.Pdf;
            }
        }

        switch (source) {
            case WebsiteSource.Youtube: {
                if (id.Length > 0) {
                    string res = GetTranscriptAsync(id).GetAwaiter().GetResult().Replace("[Music]", "");
                    Console.WriteLine(res);
                }
                else {
                    Console.Error.WriteLine($"Could not get YouTube video ID from `{id}`");
                }

                break;
            }
            case WebsiteSource.Git: {
                var res = GetGitRepoAsync(url, "*.c",
                        // "*.md,*.s,*.c,*.h,*.cpp,*.hpp,*.txt,*.css,*.json,*.js,*Makefile," +
                        // "*.png,*.jpg,*.jpeg,*.tiff,*.bmp",
                        "",
                        22_000).GetAwaiter()
                    .GetResult();
                Console.WriteLine(res);
                break;
            }
            case WebsiteSource.Pdf: {
                var res = GetPdfAsync(url).GetAwaiter().GetResult();
                Console.WriteLine(res);
                break;
            }
            case WebsiteSource.Other: {
                StringBuilder str = new();
                if (url.StartsWith("http") || url.StartsWith("www.")) {
                    using (HttpClient client = new HttpClient()) {
                        str = new StringBuilder(client.GetStringAsync(url).Result);
                    }
                }
                else if (File.Exists(url)) {
                    str = new StringBuilder(File.ReadAllText(url));
                }

                // Double spaces to single
                // This is stupid
                str.Replace("     ", " ");
                str.Replace("    ", " ");
                str.Replace("   ", " ");
                str.Replace("  ", " ");
                str.Replace("\n", "");
                str.Replace("\t", "");
                str.Replace("\r", "");

                Console.WriteLine(str);
                str.Clear();

                break;
            }
        }
    }
}