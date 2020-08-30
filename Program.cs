using HandlebarsDotNet;
using LibGit2Sharp;
using Serilog;
using Serilog.Exceptions;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

/* Taiga Body format:
 * {"title":"%title%","url":"%animeurl%","image":"%image%","total_eps":%total%,"watched_eps":%watched%,"rewatching":$if(%rewatching%,true,false),"current_ep":{"id":%episode%,"title":"%name%"}}
*/


var logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .Enrich.WithExceptionDetails()
    .Enrich.WithDemystifiedStackTraces()
    .WriteTo.Async(c => c.Console())
    .WriteTo.Async(c => c.File("log.txt"))
    .CreateLogger();

#region Argument Processing
if (args.Length < 3 || args.Length > 4)
{
    return PrintHelp();
}

var templateFile = args[0];
if (!File.Exists(templateFile))
{
    Console.Error.WriteLine($"File {templateFile} does not exist!");
    return PrintHelp();
}

var targetFile = new FileInfo(args[1]);
var repoPath = args[2];
if (!Directory.Exists(Path.Combine(repoPath, ".git")))
{
    Console.Error.WriteLine($"Repository {repoPath} does not exist!");
    return PrintHelp();
}

{
    bool hasAsParent = false;
    var target = new DirectoryInfo(repoPath);
    var dir = targetFile.Directory;
    while (dir != null)
    {
        if (dir.FullName == target.FullName)
        {
            hasAsParent = true;
            break;
        }
        dir = dir.Parent;
    }

    if (!hasAsParent)
    {
        Console.Error.WriteLine($"File {targetFile.FullName} not in {target.FullName}!");
        return 1;
    }
}

var remote = "origin";
if (args.Length > 3)
{
    remote = args[3];
}

static int PrintHelp()
{
    var name = Environment.GetCommandLineArgs()[0];
    Console.WriteLine($"Usage: {name} <template> <target> <repo> [<remote>]");
    Console.WriteLine();
    Console.WriteLine("Details:");
    Console.WriteLine("    <template>   The Handlebars template file to use to generate each update.");
    Console.WriteLine("                 In the template, you have access to the following properties:");
    Console.WriteLine("                   - Title         = The show title.");
    Console.WriteLine("                   - Url            = The show URL. This will point to a tracker like AniList.");
    Console.WriteLine("                   - ImageUrl       = The show's image URL. This will point be the show's cover art.");
    Console.WriteLine("                   - TotalEps       = The total number of episodes in the show.");
    Console.WriteLine("                   - WatchedEps     = The number of episodes the user has watched.");
    Console.WriteLine("                   - Rewatching     = Whether or not the user is rewatching the show.");
    Console.WriteLine("                   - CurrentEpisode = Information about the current episode.");
    Console.WriteLine("                         This is an object with the following properties:");
    Console.WriteLine("                           - Title  = The title of the episode.");
    Console.WriteLine("                           - Number = The episode number.");
    Console.WriteLine();
    Console.WriteLine("    <target>     The file to write the filled in template to.");
    Console.WriteLine("                 This file must be within <repo>.");
    Console.WriteLine();
    Console.WriteLine("    <repo>       The Git repository to commit and push.");
    Console.WriteLine();
    Console.WriteLine("    <remote>     The remote to push to.");
    Console.WriteLine();
    return 1;
}
#endregion

Func<TaigaInfo, string> handlebarsTemplate;
Repository _repo;
try
{
    handlebarsTemplate = Handlebars.Compile(File.ReadAllText(templateFile));
    _repo = new Repository(repoPath);
}
catch (Exception e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
using var repo = _repo;

#region Find git
string gitPath;
{
    var gitFile = Environment.GetEnvironmentVariable("PATH")!
        .Split(Path.PathSeparator)
        .Select(p => new DirectoryInfo(p))
        .Where(d => d.Exists)
        .SelectMany(d => d.EnumerateFiles("git*"))
        .FirstOrDefault(f => 
        {
            try
            {
                var proc = Process.Start(new ProcessStartInfo 
                {
                    FileName = f.FullName, 
                    Arguments = "--help",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                });
                if (proc == null) return false;
                proc.WaitForExit();
                return proc.ExitCode == 0;
            }
            catch 
            { 
                return false; 
            }
        });

    if (gitFile == null)
    {
        Console.Error.WriteLine("Cannot find git on PATH, is it installed?");
        return 1;
    }

    gitPath = gitFile.FullName;
}
#endregion

#region Server Setup
const int ListenPort = 9797;
var address = $"http://localhost:{ListenPort}/";

using var runSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    //args.Cancel = true;

    runSource.Cancel();
};

using var listener = new HttpListener();
listener.Prefixes.Add(address);
listener.Start();

logger.Information("Listening on {Address}", address);

try
{
    await Task.Run(() => Listen(logger, listener, runSource.Token), runSource.Token);
}
catch (TaskCanceledException)
{

}

return 0;

async Task Listen(ILogger logger, HttpListener listener, CancellationToken token)
{
    while (!token.IsCancellationRequested)
    {
        var ctx = await listener.GetContextAsync();
        await SafeHandleRequest(logger, ctx, token);
    }
}

async Task SafeHandleRequest(ILogger logger, HttpListenerContext context, CancellationToken token)
{
    var req = context.Request;
    using var resp = context.Response;

    logger = logger
        .ForContext("RequestMethod", req.HttpMethod)
        .ForContext("RequestUrl", req.Url);

    try
    {
        logger.Information("{Method} {Url}", req.HttpMethod, req.Url);
        await HandleRequest(logger, req, resp, token);
    }
    catch (Exception e)
    {
        logger.Error(e, "Error while processing {Method} request for {Url}", req.HttpMethod, req.Url);
        resp.StatusCode = 500;
    }
}
#endregion

async Task HandleRequest(ILogger logger, HttpListenerRequest req, HttpListenerResponse resp, CancellationToken token)
{
    logger.Debug("User Host: {UserHostName}", req.UserHostName);
    logger.Debug("User Agent: {UserAgent}", req.UserAgent);

    if (req.HttpMethod == "POST" && req.Url.PathAndQuery == "/taiga")
    {
        try
        {
            var info = await JsonSerializer.DeserializeAsync<TaigaInfo>(req.InputStream, cancellationToken: token);

            info = info.Decoded();

            logger.Information("Info: {@TaigaInfo}", info);

            using (var file = targetFile.Open(FileMode.Create, FileAccess.Write))
            using (var writer = new StreamWriter(file, Encoding.UTF8))
                await writer.WriteAsync(handlebarsTemplate(info));

            repo.Index.Add(Path.GetRelativePath(repo.Info.WorkingDirectory, targetFile.FullName));
            repo.Index.Write();

            var authorSig = new Signature("Taiga Anime Updates", "@taiga_updates", DateTime.Now);

            _ = repo.Commit("Updated currently watching", authorSig, authorSig);

            var proc = new Process();
            proc.StartInfo = new ProcessStartInfo()
            {
                FileName = gitPath,
                WorkingDirectory = repo.Info.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            proc.StartInfo.ArgumentList.Add("push");
            proc.StartInfo.ArgumentList.Add(remote);

            proc.OutputDataReceived += (_, args) =>
            {
                if (args.Data == null) return;
                logger.Debug("Git: {OutputLine}", args.Data);
            };
            proc.ErrorDataReceived += (_, args) =>
            {
                if (args.Data == null) return;
                logger.Error("Git: {OutputLine}", args.Data);
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            resp.StatusCode = 200;
        }
        catch (EmptyCommitException)
        {
            resp.StatusCode = 200;
        }
        catch (Exception e)
        {
            logger.Error(e, "Invalid input for /taiga path");
            resp.StatusCode = 400;
        }
        return;
    }

    resp.StatusCode = 404;
}

record TaigaInfo
{
    [JsonPropertyName("title")]
    public string Title { get; init; } = "";
    [JsonPropertyName("url")]
    public string Url { get; init; } = "";
    [JsonPropertyName("image")]
    public string ImageUrl { get; init; } = "";
    [JsonPropertyName("total_eps")]
    public int TotalEps { get; init; }
    [JsonPropertyName("watched_eps")]
    public int WatchedEps { get; init; }
    [JsonPropertyName("rewatching")]
    public bool Rewatching { get; init; }

    [JsonPropertyName("current_ep")]
    public Episode CurrentEpisode { get; init; } = new Episode();

    public record Episode
    {
        [JsonPropertyName("title")]
        public string Title { get; init; } = "";
        [JsonPropertyName("id")]
        public int Number { get; init; }

        public Episode Decoded()
            => this with { Title = Uri.UnescapeDataString(Title) };
    }

    public TaigaInfo Decoded()
        => this with
        {
            Title = Uri.UnescapeDataString(Title),
            Url = Uri.UnescapeDataString(Url),
            ImageUrl = Uri.UnescapeDataString(ImageUrl),
            CurrentEpisode = CurrentEpisode.Decoded()
        };
}