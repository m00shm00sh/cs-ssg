using System.Diagnostics;
using OneOf;
using OneOf.Types;

namespace CsSsg.Blog;

internal class FileContentSource(MarkdownHandler handler) : IContentSource
{
    public async Task<IEnumerable<IContentSource.Entry>> GetAvailableContentAsync(Guid? userId,
        DateTimeOffset beforeOrAt, int limit, CancellationToken ct)
    {
        var files = await Task.Run(() =>
                new DirectoryInfo("content/blog")
                    .EnumerateFiles("*.md")
                    .Where(f => f.LastWriteTimeUtc <= beforeOrAt)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(limit),
            ct);
        return files.Select(async f =>
            {
                var mdName = f.Name[..^".md".Length];
                var contents = await GetContentAsync(null, mdName, ct);
                if (contents is null)
                {
                    Debug.Fail("could not open file");
                    throw new InvalidOperationException();
                }

                var title = handler.GetMarkdownTitle(contents.Value.Body, mdName, ct)
                            ?? $"[Error: could not determine title for {mdName}]";
                return new IContentSource.Entry(title!, mdName, f.LastWriteTimeUtc);
            })
            .Select(t => t.Result);
    }

    public async Task<IContentSource.AccessLevel?> GetPermissionsForContentAsync(Guid? userId, string name,
        CancellationToken ct)
        => await Task.Run(() =>
        {
            try
            {
                var attrs = File.GetAttributes(_nameToFile(name));
                return (attrs & FileAttributes.ReadOnly) == FileAttributes.ReadOnly
                    ? IContentSource.AccessLevel.Read
                    : IContentSource.AccessLevel.Write;
            }
            catch (FileNotFoundException)
            {
                return (IContentSource.AccessLevel?)null;
            }
        }, cancellationToken: ct);

    public async Task<IContentSource.Contents?> GetContentAsync(Guid? userId, string name, CancellationToken ct)
        => (await _tryIO(() =>
            File.ReadAllTextAsync(_nameToFile(name), ct))
        ).Match(
            (string content) => new IContentSource.Contents(null, content),
            (Error _) => (IContentSource.Contents?)null
        );

    public async Task<bool> SetContentAsync(Guid? userId, string name, string content,
        CancellationToken ct)
        => (await _tryIO(() =>
            File.WriteAllTextAsync(_nameToFile(name), content, ct))
        ).Match(
            (Success _) => true,
            (Error _) => false
        );

    public async Task<bool> DeleteContentAsync(Guid? userId, string name,
        CancellationToken ct)
        => (await _tryIO(() =>
            Task.Run(() => 
                File.Delete(_nameToFile(name)), ct)
            )
        ).Match(
            (Success _) => true,
            (Error _) => false
        );

    private static string _nameToFile(string name)
        => $"content/blog/{name}.md";
    
    private static async Task<OneOf<T, Error>> _tryIO<T>(Func<Task<T>> func)
    {
        try
        {
            return await func();
        }
        catch (FileNotFoundException)
        {
            return new Error();
        }
        catch (UnauthorizedAccessException)
        {
            return new Error();
        }
    }
    
    private static async Task<OneOf<Success, Error>> _tryIO(Func<Task> func)
    => await _tryIO(async () =>
    {
        await func();
        return new Success();
    });
}