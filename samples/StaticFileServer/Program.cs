using System.Net;
using System.Net.Sockets;
using System.Text;
using Cap.Primitives;
using Cap.Std;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace StaticFileServer;

/// <summary>
/// Serves a directory over HTTP, where no request path can reach anything outside it.
/// </summary>
/// <remarks>
/// <para>
/// A static file host is where the string check in the README most often lives: the request
/// path is joined onto the content root, normalised, compared with the root, and opened.
/// Here the request path goes, as it arrived, to a <see cref="Dir"/> on the content root, and
/// a path that would resolve anywhere else — through <c>..</c>, an absolute path, a Windows
/// device name, or a symbolic link someone left in the content — is refused by the
/// resolution. There is no check in this file to get wrong.
/// </para>
/// <para>
/// Run as <c>StaticFileServer &lt;directory&gt;</c> it serves that directory. Run with no
/// arguments it builds a content root of its own with a link inside pointing out, starts on a
/// loopback port, sends itself hostile requests over a raw socket (so that no client library
/// tidies the paths first), and exits non-zero if any of them reached the file outside.
/// </para>
/// </remarks>
internal static class Program
{
    private const string Secret = "outside the content root";

    internal static async Task<int> Main(string[] args)
    {
        switch (args)
        {
            case [string contentRoot]:
                // The composition root: the only authority this program takes is the one
                // directory it was told to serve.
                using (Dir content = Dir.Open(contentRoot, AmbientAuthority.Acquire()))
                {
                    WebApplication app = Build(content, url: null);
                    await app.RunAsync();
                    return 0;
                }

            case []:
                return await DemonstrateAsync();

            default:
                Console.Error.WriteLine("usage: StaticFileServer [directory]");
                return 2;
        }
    }

    /// <summary>The application: one route, everything served from <paramref name="content"/>.</summary>
    internal static WebApplication Build(Dir content, string? url)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        if (url is not null)
        {
            builder.WebHost.UseUrls(url);
        }

        WebApplication app = builder.Build();
        FileExtensionContentTypeProvider contentTypes = new();

        app.MapGet("/{**path}", (string? path) => Serve(content, path ?? string.Empty, contentTypes));
        return app;
    }

    /// <summary>
    /// Answers one request.
    /// </summary>
    /// <remarks>
    /// Every refusal is a 404, whatever its reason. Telling a client that a path was refused as
    /// an escape, rather than not found, would tell it something about what lies outside.
    /// </remarks>
    private static IResult Serve(Dir content, string path, FileExtensionContentTypeProvider contentTypes)
    {
        if (path.Length == 0 || path.EndsWith('/') ||
            (content.TryGetMetadata(path, out CapMetadata metadata) && metadata.Type == CapFileType.Directory))
        {
            path = path.TrimEnd('/') is { Length: > 0 } directory ? directory + "/index.html" : "index.html";
        }

        try
        {
            CapFile file = content.OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
            string contentType = contentTypes.TryGetContentType(path, out string? known) ? known : "application/octet-stream";

            // The stream owns the handle from here, and the response disposes the stream.
            return Results.Stream(file.AsStream(leaveOpen: false), contentType);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // SandboxEscapeException is an IOException, and is answered the same way.
            return Results.NotFound();
        }
    }

    /// <summary>
    /// Serves a content root with a link leading out of it, and attacks it.
    /// </summary>
    private static async Task<int> DemonstrateAsync()
    {
        string scratch = Directory.CreateTempSubdirectory("cap-static-file-server-").FullName;
        try
        {
            string root = Directory.CreateDirectory(Path.Combine(scratch, "wwwroot")).FullName;
            string outside = Directory.CreateDirectory(Path.Combine(scratch, "private")).FullName;
            File.WriteAllText(Path.Combine(root, "index.html"), "<h1>hello</h1>");
            Directory.CreateDirectory(Path.Combine(root, "docs"));
            File.WriteAllText(Path.Combine(root, "docs", "index.html"), "<h1>docs</h1>");
            File.WriteAllText(Path.Combine(root, "docs", "guide.txt"), "a guide");
            File.WriteAllText(Path.Combine(outside, "secret.txt"), Secret);

            try
            {
                Directory.CreateSymbolicLink(Path.Combine(root, "assets"), outside);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                Console.Error.WriteLine($"This account cannot create symbolic links here: {e.Message}");
                return 3;
            }

            int port = FreeLoopbackPort();
            using Dir content = Dir.Open(root, AmbientAuthority.Acquire());
            await using WebApplication app = Build(content, $"http://127.0.0.1:{port}");
            await app.StartAsync();

            Console.WriteLine($"Serving {root} on http://127.0.0.1:{port}");
            Console.WriteLine($"  (wwwroot/assets is a link to {outside})");
            Console.WriteLine();

            (string Target, bool ShouldServe)[] requests =
            [
                ("/", true),
                ("/docs/guide.txt", true),
                ("/docs/", true),
                ("/assets/secret.txt", false),
                ("/../private/secret.txt", false),
                ("/%2e%2e/private/secret.txt", false),
                ("/docs/..%2f..%2fprivate%2fsecret.txt", false),
                ("/docs%5c..%5c..%5cprivate%5csecret.txt", false),
                ("//etc/passwd", false),
            ];

            bool failed = false;
            foreach ((string target, bool shouldServe) in requests)
            {
                (int status, string body) = await RawGetAsync(port, target);
                bool leaked = body.Contains(Secret, StringComparison.Ordinal);
                bool served = status == 200;
                failed |= leaked || served != shouldServe;
                Console.WriteLine($"  {status}  GET {target}{(leaked ? "   <-- LEAKED" : string.Empty)}");
            }

            await app.StopAsync();
            Console.WriteLine();
            Console.WriteLine(failed ? "A request was not answered as expected." : "Nothing outside the content root was served.");

            await CompareWithPhysicalFileProviderAsync(root);
            return failed ? 1 : 0;
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// Serves the same content root the way ASP.NET Core does by default, and makes the one
    /// request that matters.
    /// </summary>
    /// <remarks>
    /// For comparison only, and not part of the exit status: what this shows is a property of
    /// another library, which is free to change.
    /// </remarks>
    private static async Task CompareWithPhysicalFileProviderAsync(string root)
    {
        int port = FreeLoopbackPort();
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        await using WebApplication app = builder.Build();
        using PhysicalFileProvider provider = new(root);
        app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
        await app.StartAsync();

        (int status, string body) = await RawGetAsync(port, "/assets/secret.txt");
        await app.StopAsync();

        Console.WriteLine();
        Console.WriteLine("For comparison, the same content root served by UseStaticFiles over PhysicalFileProvider:");
        Console.WriteLine(
            $"  {status}  GET /assets/secret.txt" +
            (body.Contains(Secret, StringComparison.Ordinal) ? $"   <-- served \"{Secret}\"" : string.Empty));
    }

    /// <summary>
    /// Sends a request exactly as written, which an HTTP client library would not: they
    /// resolve dot segments and re-encode the path before it leaves.
    /// </summary>
    private static async Task<(int Status, string Body)> RawGetAsync(int port, string target)
    {
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using NetworkStream stream = client.GetStream();

        byte[] request = Encoding.ASCII.GetBytes(
            $"GET {target} HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);

        using StreamReader reader = new(stream, Encoding.UTF8);
        string response = await reader.ReadToEndAsync();

        string[] statusLine = response.Split("\r\n", 2)[0].Split(' ');
        int status = statusLine.Length > 1 && int.TryParse(statusLine[1], out int code) ? code : 0;
        return (status, response);
    }

    private static int FreeLoopbackPort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
