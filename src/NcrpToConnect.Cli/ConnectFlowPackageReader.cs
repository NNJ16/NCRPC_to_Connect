using System.IO.Compression;

namespace NcrpToConnect.Cli;

internal sealed class ConnectFlowPackageReader
{
    private readonly ConnectFlowReader flowReader = new();

    public ConnectFlowPackage Read(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Input .wap file was not found.", path);
        }

        var fullPath = Path.GetFullPath(path);
        using var archive = ZipFile.OpenRead(fullPath);
        return ReadArchive(fullPath, archive);
    }

    public ConnectFlowPackage Read(Stream stream, string sourcePath)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        return ReadArchive(sourcePath, archive);
    }

    private ConnectFlowPackage ReadArchive(string sourcePath, ZipArchive archive)
    {
        var entries = archive.Entries
            .Where(entry => string.Equals(Path.GetExtension(entry.FullName), ".flow", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(entry => ReadEntry(sourcePath, entry))
            .ToArray();

        if (entries.Length == 0)
        {
            throw new InvalidDataException("The .wap package does not contain any .flow entries.");
        }

        return new ConnectFlowPackage(sourcePath, entries);
    }

    private ConnectFlowPackageEntry ReadEntry(string packagePath, ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        var sourcePath = $"{packagePath}!/{entry.FullName}";
        return new ConnectFlowPackageEntry(entry.FullName, flowReader.ReadXml(sourcePath, reader.ReadToEnd()));
    }
}
