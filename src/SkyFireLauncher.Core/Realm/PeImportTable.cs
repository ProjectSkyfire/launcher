namespace SkyFireLauncher.Realm;

// Minimal PE import-directory reader over a file-on-disk buffer. Finds the
// RVA of the IAT slot for a given "dll.dll"/"FunctionName" import; adding
// that RVA to a loaded module's base address in a live process gives the
// address to overwrite to redirect every call the module makes through
// that import.
//
// Reads the exe FILE, not a live/loaded module buffer: once loaded, the
// Windows loader overwrites FirstThunk (the IAT) with resolved function
// addresses, and not every import descriptor keeps a separate, still-RVA
// OriginalFirstThunk - reading that from live memory can misinterpret a
// resolved pointer as an unresolved RVA. RVAs themselves are identical
// between the file and the loaded image (ASLR only changes the base, not
// internal offsets), but the file's raw byte layout does NOT match the
// loaded image's layout - every RVA read from a file buffer has to be
// translated to a file offset via the section table first.
public static class PeImportTable
{
    public static bool IsPe64Bit(byte[] module)
    {
        // The DOS/PE headers themselves are always mapped 1:1 at the start
        // of both the file and the loaded image, so no RVA translation is
        // needed to read them.
        var e_lfanew = ReadInt32(module, 0x3C);
        var machine = ReadUInt16(module, e_lfanew + 4);
        return machine == 0x8664; // IMAGE_FILE_MACHINE_AMD64
    }

    public static int GetSizeOfImage(byte[] module)
    {
        var e_lfanew = ReadInt32(module, 0x3C);
        var optionalHeaderStart = e_lfanew + 24;
        return ReadInt32(module, optionalHeaderStart + 56);
    }

    /// <summary>
    /// Builds a SizeOfImage-sized buffer with PE sections at their RVAs (no
    /// relocations applied). Safe to search for code/string patterns that are
    /// valid at load time before fixups — avoids reading the live process image.
    /// </summary>
    public static byte[] BuildVirtualImage(byte[] file)
    {
        var sizeOfImage = GetSizeOfImage(file);
        if (sizeOfImage <= 0 || sizeOfImage > 512 * 1024 * 1024)
            throw new InvalidOperationException("PE SizeOfImage is invalid.");

        var image = new byte[sizeOfImage];
        var e_lfanew = ReadInt32(file, 0x3C);
        var fileHeaderStart = e_lfanew + 4;
        var numberOfSections = ReadUInt16(file, fileHeaderStart + 2);
        var sizeOfOptionalHeader = ReadUInt16(file, fileHeaderStart + 16);
        var sizeOfHeaders = ReadInt32(file, e_lfanew + 24 + 60);
        var headerCopy = Math.Min(Math.Min(sizeOfHeaders, file.Length), sizeOfImage);
        Buffer.BlockCopy(file, 0, image, 0, headerCopy);

        var sectionTableStart = e_lfanew + 24 + sizeOfOptionalHeader;
        for (var i = 0; i < numberOfSections; i++)
        {
            var entryStart = sectionTableStart + i * 40;
            var virtualAddress = ReadInt32(file, entryStart + 12);
            var sizeOfRawData = ReadInt32(file, entryStart + 16);
            var pointerToRawData = ReadInt32(file, entryStart + 20);
            if (virtualAddress < 0 || pointerToRawData < 0 || sizeOfRawData <= 0)
                continue;

            var dest = virtualAddress;
            var availFile = Math.Max(0, file.Length - pointerToRawData);
            var availImage = Math.Max(0, sizeOfImage - dest);
            var copy = Math.Min(sizeOfRawData, Math.Min(availFile, availImage));
            if (copy <= 0)
                continue;

            Buffer.BlockCopy(file, pointerToRawData, image, dest, copy);
        }

        return image;
    }

    public static int FindIatSlotRva(byte[] module, string dllName, string functionName, int knownOrdinal = 0)
    {
        var e_lfanew = ReadInt32(module, 0x3C);
        var fileHeaderStart = e_lfanew + 4;
        var machine = ReadUInt16(module, fileHeaderStart);
        var is64Bit = machine == 0x8664;
        var numberOfSections = ReadUInt16(module, fileHeaderStart + 2);
        var sizeOfOptionalHeader = ReadUInt16(module, fileHeaderStart + 16);

        var optionalHeaderStart = fileHeaderStart + 20;
        var magic = ReadUInt16(module, optionalHeaderStart);
        var isPe32Plus = magic == 0x20B;
        if (isPe32Plus != is64Bit)
            throw new InvalidOperationException("PE machine type and optional header magic disagree.");

        var sectionTableStart = optionalHeaderStart + sizeOfOptionalHeader;
        var sections = ReadSectionTable(module, sectionTableStart, numberOfSections);

        // DataDirectory[1] = Import Table.
        var dataDirectoryOffset = optionalHeaderStart + (isPe32Plus ? 112 : 96);
        var importDirRva = ReadInt32(module, dataDirectoryOffset + 1 * 8);

        var descriptorRva = importDirRva;
        while (true)
        {
            var descriptorOffset = RvaToFileOffset(sections, descriptorRva);
            var originalFirstThunkRva = ReadInt32(module, descriptorOffset + 0);
            var nameRva = ReadInt32(module, descriptorOffset + 12);
            var firstThunkRva = ReadInt32(module, descriptorOffset + 16);

            if (originalFirstThunkRva == 0 && nameRva == 0 && firstThunkRva == 0)
                break; // null terminator entry

            var thisDllName = ReadAsciiString(module, RvaToFileOffset(sections, nameRva));
            if (string.Equals(thisDllName, dllName, StringComparison.OrdinalIgnoreCase))
            {
                var nameThunkRva = originalFirstThunkRva != 0 ? originalFirstThunkRva : firstThunkRva;
                var iatRva = FindFunctionSlot(module, sections, nameThunkRva, firstThunkRva, functionName, knownOrdinal, isPe32Plus);
                if (iatRva != 0)
                    return iatRva;
            }

            descriptorRva += 20; // sizeof(IMAGE_IMPORT_DESCRIPTOR)
        }

        return 0;
    }

    private static int FindFunctionSlot(byte[] module, (int Rva, int Size, int FileOffset)[] sections, int nameThunkRva, int iatThunkRva, string functionName, int knownOrdinal, bool isPe32Plus)
    {
        var entrySize = isPe32Plus ? 8 : 4;
        var index = 0;

        while (true)
        {
            var nameThunkOffset = RvaToFileOffset(sections, nameThunkRva + index * entrySize);
            long thunkValue = isPe32Plus ? ReadInt64(module, nameThunkOffset) : (uint)ReadInt32(module, nameThunkOffset);

            if (thunkValue == 0)
                return 0; // end of this DLL's import list, not found

            var ordinalFlag = isPe32Plus ? (1L << 63) : (1L << 31);
            if ((thunkValue & ordinalFlag) == 0)
            {
                // thunkValue is an RVA to IMAGE_IMPORT_BY_NAME { WORD Hint; CHAR Name[]; }
                var importByNameOffset = RvaToFileOffset(sections, (int)thunkValue) + 2;
                var name = ReadAsciiString(module, importByNameOffset);
                if (string.Equals(name, functionName, StringComparison.Ordinal))
                    return iatThunkRva + index * entrySize;
            }
            else if (knownOrdinal != 0 && (thunkValue & 0xFFFF) == knownOrdinal)
            {
                // Older/classic APIs (most of Winsock 1.1, including
                // gethostbyname) are commonly imported by ordinal rather
                // than by name - there's no string to match against, so
                // this only matches when the caller supplied the known,
                // documented ordinal for the function it's looking for.
                return iatThunkRva + index * entrySize;
            }

            index++;
        }
    }

    private static (int Rva, int Size, int FileOffset)[] ReadSectionTable(byte[] module, int sectionTableStart, int numberOfSections)
    {
        var sections = new (int Rva, int Size, int FileOffset)[numberOfSections];

        for (var i = 0; i < numberOfSections; i++)
        {
            var entryStart = sectionTableStart + i * 40; // sizeof(IMAGE_SECTION_HEADER)
            var virtualSize = ReadInt32(module, entryStart + 8);
            var virtualAddress = ReadInt32(module, entryStart + 12);
            var sizeOfRawData = ReadInt32(module, entryStart + 16);
            var pointerToRawData = ReadInt32(module, entryStart + 20);

            sections[i] = (virtualAddress, Math.Max(virtualSize, sizeOfRawData), pointerToRawData);
        }

        return sections;
    }

    private static int RvaToFileOffset((int Rva, int Size, int FileOffset)[] sections, int rva)
    {
        foreach (var section in sections)
        {
            if (rva >= section.Rva && rva < section.Rva + section.Size)
                return section.FileOffset + (rva - section.Rva);
        }

        // Falls within the headers themselves (before the first section) -
        // file offset equals RVA there.
        return rva;
    }

    private static string ReadAsciiString(byte[] module, int offset)
    {
        var end = offset;
        while (end < module.Length && module[end] != 0)
            end++;

        return System.Text.Encoding.ASCII.GetString(module, offset, end - offset);
    }

    private static int ReadUInt16(byte[] buffer, int offset) => buffer[offset] | (buffer[offset + 1] << 8);

    private static int ReadInt32(byte[] buffer, int offset) =>
        buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24);

    private static long ReadInt64(byte[] buffer, int offset)
    {
        long low = (uint)ReadInt32(buffer, offset);
        long high = (uint)ReadInt32(buffer, offset + 4);
        return low | (high << 32);
    }
}
