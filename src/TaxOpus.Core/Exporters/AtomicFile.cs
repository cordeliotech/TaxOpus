using System.Text;
using System.Text.Json;

namespace TaxOpus.Core.Exporters;

internal static class AtomicFile
{
    public static async Task WriteAllTextAsync(
        string path,
        string contents,
        Encoding encoding,
        CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var part = PartPath(path);
        try
        {
            await File.WriteAllTextAsync(part, contents, encoding, cancellationToken);
            File.Move(part, path, overwrite: true);
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    public static async Task WriteStreamAsync(
        string path,
        Func<Stream, CancellationToken, Task> writeAsync,
        CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var part = PartPath(path);
        try
        {
            await using (var stream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536, useAsync: true))
            {
                await writeAsync(stream, cancellationToken);
            }
            File.Move(part, path, overwrite: true);
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    public static Task WriteJsonAsync<T>(
        string path,
        T value,
        JsonSerializerOptions options,
        CancellationToken cancellationToken = default) =>
        WriteStreamAsync(path, (stream, ct) => JsonSerializer.SerializeAsync(stream, value, options, ct), cancellationToken);

    private static string PartPath(string path) => $"{path}.{Guid.NewGuid():N}.part";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup of temp file
        }
    }
}
