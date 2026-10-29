using Microsoft.Extensions.FileProviders;

// Serves samples/web plus clients/js/papertrail.js. Reads straight from the source tree when it
// is available (edit-and-refresh), otherwise from the copies in the build output.

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://127.0.0.1:47200");

var app = builder.Build();

string? repoRoot = FindRepoRoot(AppContext.BaseDirectory);
IFileProvider files = repoRoot is null
    ? new PhysicalFileProvider(Path.Combine(AppContext.BaseDirectory, "site"))
    : new CompositeFileProvider(
        new PhysicalFileProvider(Path.Combine(repoRoot, "samples", "web")),
        new PrefixedFileProvider("lib", new PhysicalFileProvider(Path.Combine(repoRoot, "clients", "js"))));

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.CacheControl = "no-cache";
    await next();
});
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
app.UseStaticFiles(new StaticFileOptions { FileProvider = files });

app.Run();

static string? FindRepoRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "papertrail.slnx")))
        {
            return dir.FullName;
        }
    }

    return null;
}

/// <summary>Exposes an inner provider under a sub-path, e.g. "lib/papertrail.js".</summary>
internal sealed class PrefixedFileProvider(string prefix, IFileProvider inner) : IFileProvider
{
    private readonly string _prefix = "/" + prefix.Trim('/');

    public IDirectoryContents GetDirectoryContents(string subpath) =>
        Strip(subpath) is { } rest ? inner.GetDirectoryContents(rest) : NotFoundDirectoryContents.Singleton;

    public IFileInfo GetFileInfo(string subpath) =>
        Strip(subpath) is { } rest ? inner.GetFileInfo(rest) : new NotFoundFileInfo(subpath);

    public Microsoft.Extensions.Primitives.IChangeToken Watch(string filter) => inner.Watch(filter);

    private string? Strip(string subpath)
    {
        string normalized = "/" + subpath.TrimStart('/');
        if (normalized.Equals(_prefix, StringComparison.OrdinalIgnoreCase))
        {
            return "/";
        }

        return normalized.StartsWith(_prefix + "/", StringComparison.OrdinalIgnoreCase) ? normalized[_prefix.Length..] : null;
    }
}
