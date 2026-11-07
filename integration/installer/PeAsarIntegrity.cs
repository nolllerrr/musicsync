using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;

internal static class PeAsarIntegrity
{
    private const uint DirectoryFlag = 0x80000000;
    private const uint OffsetMask = 0x7fffffff;

    internal static void Build(string original, string destination, string originalHash, string patchedHash)
    {
        File.Copy(original, destination);
        using var file = File.Open(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var offset = GetHashOffset(file, originalHash);
        file.Position = offset;
        file.Write(Encoding.ASCII.GetBytes(patchedHash));
        file.Flush(flushToDisk: true);
        if (GetHashOffset(file, patchedHash) != offset)
        {
            throw new InvalidDataException("Could not verify the updated ElectronAsar resource.");
        }
    }

    private static int GetHashOffset(Stream file, string expectedHash)
    {
        file.Position = 0;
        using var pe = new PEReader(file, PEStreamOptions.LeaveOpen);
        if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64)
        {
            throw new InvalidDataException("The Yandex Music executable is not x64.");
        }

        var resources = pe.PEHeaders.PEHeader!.ResourceTableDirectory;
        var tree = pe.GetSectionData(resources.RelativeVirtualAddress).GetReader(0, resources.Size);
        var type = FindNamedEntry(tree, 0, "Integrity");
        var resource = FindNamedEntry(tree, type, "ElectronAsar");
        tree.Offset = resource + 12;
        var namedCount = tree.ReadUInt16();
        var idCount = tree.ReadUInt16();
        if (namedCount != 0 || idCount != 1)
        {
            throw new InvalidDataException("Unexpected language count in ElectronAsar resource.");
        }

        tree.Offset += 4;
        var leaf = tree.ReadInt32();
        tree.Offset = leaf;
        var address = tree.ReadInt32();
        var size = tree.ReadInt32();
        if (size <= 0 || size > 16 * 1024 ||
            !pe.PEHeaders.TryGetDirectoryOffset(new DirectoryEntry(address, size), out var resourceOffset))
        {
            throw new InvalidDataException("Invalid ElectronAsar resource location.");
        }

        var bytes = pe.GetSectionData(address).GetContent(0, size).ToArray();
        using var json = JsonDocument.Parse(bytes);
        var asar = json.RootElement.EnumerateArray().Single(item =>
            string.Equals(item.GetProperty("file").GetString()?.Replace('/', '\\'), "resources\\app.asar", StringComparison.OrdinalIgnoreCase));
        if (!string.Equals(asar.GetProperty("alg").GetString(), "sha256", StringComparison.OrdinalIgnoreCase) ||
            asar.GetProperty("value").GetString() != expectedHash)
        {
            throw new InvalidDataException("The ElectronAsar resource does not match the ASAR header.");
        }

        var hashBytes = Encoding.ASCII.GetBytes(expectedHash);
        var relativeOffset = bytes.AsSpan().IndexOf(hashBytes);
        if (relativeOffset < 0 || bytes.AsSpan(relativeOffset + hashBytes.Length).IndexOf(hashBytes) >= 0)
        {
            throw new InvalidDataException("The ElectronAsar hash has no unique resource offset.");
        }
        return resourceOffset + relativeOffset;
    }

    private static int FindNamedEntry(BlobReader tree, int directory, string wanted)
    {
        tree.Offset = directory + 12;
        var namedCount = tree.ReadUInt16();
        for (var index = 0; index < namedCount; index++)
        {
            tree.Offset = directory + 16 + index * 8;
            var name = tree.ReadUInt32();
            var child = tree.ReadUInt32();
            if ((name & DirectoryFlag) == 0 || (child & DirectoryFlag) == 0)
            {
                throw new InvalidDataException("Unexpected PE resource directory entry.");
            }

            tree.Offset = (int)(name & OffsetMask);
            var byteLength = 2 * tree.ReadUInt16();
            if (string.Equals(tree.ReadUTF16(byteLength), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return (int)(child & OffsetMask);
            }
        }
        throw new InvalidDataException($"PE resource directory {wanted} was not found.");
    }
}
