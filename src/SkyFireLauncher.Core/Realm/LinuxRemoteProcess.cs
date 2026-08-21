using System.Globalization;
using System.Runtime.InteropServices;

namespace SkyFireLauncher.Realm;

internal sealed class LinuxRemoteProcess : IRemoteProcess
{
    private const int SysMmap = 9;
    private const int SysMmap2 = 192;
    private const int SysMprotect = 10;
    private const int SysMprotect32 = 125;
    private const int ProtReadWriteExec = 7; // PROT_READ | PROT_WRITE | PROT_EXEC
    private const int MapPrivateAnonymous = 0x22;
    private const int Map32Bit = 0x40;
    private const int SigKill = 9;
    private const int PageSize = 4096;

    private readonly List<int> _attachedTids = [];
    private readonly bool _is64Bit;
    private nint _syscallGadget;
    private bool _disposed;

    public int Id { get; }

    public LinuxRemoteProcess(int pid)
    {
        Id = pid;
        AttachAllThreads();
        _is64Bit = IsElf64(pid);
    }

    public byte[] Read(nint address, int size)
    {
        var buffer = new byte[size];
        var offset = 0;
        while (offset < size)
        {
            var chunk = Math.Min(4096, size - offset);
            var slice = buffer.AsSpan(offset, chunk);
            try
            {
                if (!TryProcessVm(write: false, address + offset, slice))
                    ReadProcMem(address + offset, slice);
            }
            catch
            {
                // Unmapped pages stay zeroed - pattern search skips them.
            }
            offset += chunk;
        }

        return buffer;
    }

    public void Write(nint address, byte[] data)
    {
        if (data.Length == 0)
            return;

        // Hostname/login patches live in read-only PE sections. process_vm_writev
        // and /proc/pid/mem both return EIO/EFAULT on those pages unless the
        // mapping is made writable first. Prefer mprotect + writev, then ptrace.
        if (TryWrite(address, data))
            return;

        try
        {
            Unprotect(address, data.Length);
        }
        catch
        {
            // Still try poke if mprotect is unavailable.
        }

        if (TryWrite(address, data))
            return;

        WriteViaPtrace(address, data);
    }

    public nint AllocateExecutable(int size, bool prefer32BitAddress)
    {
        var length = Math.Max(PageSize, (size + PageSize - 1) & ~(PageSize - 1));
        var gadget = EnsureSyscallGadget();
        var tid = _attachedTids[0];

        long result;
        if (_is64Bit)
        {
            var flags = (ulong)MapPrivateAnonymous;
            if (prefer32BitAddress)
                flags |= Map32Bit;

            result = (long)InjectSyscall64(
                tid,
                gadget,
                SysMmap,
                rdi: 0,
                rsi: (ulong)length,
                rdx: ProtReadWriteExec,
                r10: flags,
                r8: ulong.MaxValue,
                r9: 0);
        }
        else
        {
            result = InjectSyscall32Mmap2(tid, gadget, length);
        }

        if (result is < 0 and > -4096)
            throw new InvalidOperationException($"mmap in the client process failed (errno {-result}).");

        return (nint)result;
    }

    public void Terminate() => Native.kill(Id, SigKill);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        DetachAllThreads();
    }

    public static int WaitForMappedModule(string exePath, int rootPid, TimeSpan timeout)
    {
        var fileName = Path.GetFileName(exePath);
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            foreach (var pid in EnumerateDescendantsAndSelf(rootPid))
            {
                if (MapsContain(pid, exePath, fileName))
                    return pid;
            }

            // Proton's python wrapper can exit after spawning Wine, so also
            // scan every process for the mapped client PE.
            foreach (var pid in EnumeratePids())
            {
                if (MapsContain(pid, exePath, fileName))
                    return pid;
            }

            // If the launcher process died and nothing mapped the client, fail fast.
            if (!Directory.Exists($"/proc/{rootPid}"))
            {
                // Give a brief grace period for reparented wine children.
                Thread.Sleep(200);
                foreach (var pid in EnumeratePids())
                {
                    if (MapsContain(pid, exePath, fileName))
                        return pid;
                }

                throw new InvalidOperationException(
                    $"Wine/Proton exited before {fileName} started. Check skyfire-launch.log and that GE-Proton's wine binary can open a display.");
            }

            Thread.Sleep(50);
        }

        throw new InvalidOperationException(
            $"Timed out waiting for {fileName} to appear in a Wine/Proton process. Is the compatibility layer installed and able to start this client?");
    }

    /// <summary>
    /// Waits for the real game process: cmdline mentions the client exe and the
    /// PE is mapped. Do not require d3d9 yet — we patch before graphics init so
    /// ptrace does not freeze DXVK startup (alive process, no window).
    /// </summary>
    public static int WaitForReadyClient(string exePath, int rootPid, TimeSpan timeout)
    {
        var fileName = Path.GetFileName(exePath);
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            foreach (var pid in EnumerateCandidateClientPids(rootPid))
            {
                if (!CommandLineMentions(pid, fileName))
                    continue;
                if (!MapsContain(pid, exePath, fileName))
                    continue;
                return pid;
            }

            if (!Directory.Exists($"/proc/{rootPid}"))
            {
                Thread.Sleep(200);
                foreach (var pid in EnumeratePids())
                {
                    if (CommandLineMentions(pid, fileName) && MapsContain(pid, exePath, fileName))
                        return pid;
                }

                throw new InvalidOperationException(
                    $"Wine/Proton exited before {fileName} started. Check skyfire-launch.log.");
            }

            Thread.Sleep(50);
        }

        throw new InvalidOperationException(
            $"Timed out waiting for {fileName} to start. Check display/Vulkan and skyfire-launch.log.");
    }

    public static bool CommandLineMentions(int pid, string token)
    {
        try
        {
            var raw = File.ReadAllText($"/proc/{pid}/cmdline");
            if (string.IsNullOrEmpty(raw))
                return false;

            var cmdline = raw.Replace('\0', ' ');
            return cmdline.Contains(token, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static bool MapsContainDll(int pid, string dllFileName)
    {
        foreach (var map in ParseMaps(pid))
        {
            if (string.IsNullOrEmpty(map.Path))
                continue;

            var mapFileName = Path.GetFileName(map.Path.Replace('\\', '/'));
            if (mapFileName.Equals(dllFileName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static IEnumerable<int> EnumerateCandidateClientPids(int rootPid)
    {
        foreach (var pid in EnumerateDescendantsAndSelf(rootPid))
            yield return pid;

        foreach (var pid in EnumeratePids())
            yield return pid;
    }

    public static void WaitForMappedDll(int pid, string dllFileName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!Directory.Exists($"/proc/{pid}"))
                throw new InvalidOperationException("The client process exited before graphics DLLs finished loading.");

            if (MapsContainDll(pid, dllFileName))
                return;

            Thread.Sleep(50);
        }

        throw new InvalidOperationException(
            $"Timed out waiting for {dllFileName} in pid {pid}. Attached process is probably not the running game.");
    }

    public static (nint BaseAddress, int ModuleSize) FindPeModule(int pid, string exePath, byte[] fileBuffer)
    {
        var fileName = Path.GetFileName(exePath);
        MapRange? headerMap = null;

        foreach (var map in ParseMaps(pid))
        {
            if (string.IsNullOrEmpty(map.Path))
                continue;

            var mapFileName = Path.GetFileName(map.Path.Replace('\\', '/'));
            var isMatch = map.Path.Equals(exePath, StringComparison.OrdinalIgnoreCase)
                || mapFileName.Equals(fileName, StringComparison.OrdinalIgnoreCase);
            if (!isMatch)
                continue;

            if (map.FileOffset == 0)
            {
                headerMap = map;
                break;
            }

            headerMap ??= map;
        }

        if (headerMap is null)
            throw new InvalidOperationException($"Could not find module '{fileName}' in the client process.");

        return (headerMap.Value.Start, PeImportTable.GetSizeOfImage(fileBuffer));
    }

    public static string ResolveWineBinary(bool is64BitClient)
    {
        string[] names = is64BitClient
            ? ["wine64", "wine64-stable", "wine", "wine-stable"]
            : ["wine", "wine-stable", "wine32", "wine64", "wine64-stable"];

        foreach (var name in names)
        {
            var found = FindOnPath(name);
            if (found is not null)
                return found;
        }

        throw new InvalidOperationException(
            "Wine was not found on PATH. Install Wine to launch the Windows client from Linux.");
    }

    private void AttachAllThreads()
    {
        // Only attach the thread-group leader. Stopping every Wine thread while
        // D3D/DXVK is starting routinely leaves Wow alive but with no window.
        if (!Directory.Exists($"/proc/{Id}"))
            throw new InvalidOperationException("The client process exited before it could be patched.");

        if (Native.ptrace(Native.PTRACE_ATTACH, Id, 0, 0) != 0)
        {
            throw new InvalidOperationException(
                "Could not attach to the client process (ptrace). " +
                "Steam Linux Runtime / *-slr Proton builds block host ptrace — pick GE-Proton or proton-cachyos-native. " +
                "Also check kernel.yama.ptrace_scope (0 or 1) and run the launcher as the same user that owns the Wine process.");
        }

        if (!WaitForStop(Id, TimeSpan.FromSeconds(3)))
        {
            Native.ptrace(Native.PTRACE_DETACH, Id, 0, 0);
            throw new InvalidOperationException(
                "ptrace attached but the client thread did not stop. Try PLAY again, or pick a different Proton build.");
        }

        _attachedTids.Add(Id);
    }

    private static bool WaitForStop(int tid, TimeSpan timeout)
    {
        const int wnohang = 1;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var result = Native.waitpid(tid, out _, wnohang);
            if (result == tid)
                return true;
            if (result < 0)
                return false;

            Thread.Sleep(10);
        }

        return false;
    }

    private void DetachAllThreads()
    {
        foreach (var tid in _attachedTids)
        {
            // Continue first so a leftover SIGTRAP/SIGSTOP cannot kill the client
            // right after a successful patch.
            Native.ptrace(Native.PTRACE_CONT, tid, 0, 0);
            Native.ptrace(Native.PTRACE_DETACH, tid, 0, 0);
        }

        _attachedTids.Clear();
    }

    private nint EnsureSyscallGadget()
    {
        if (_syscallGadget == 0)
            _syscallGadget = FindSyscallGadget(_is64Bit);
        return _syscallGadget;
    }

    private nint FindSyscallGadget(bool processIs64Bit)
    {
        byte[] needle = processIs64Bit ? [0x0F, 0x05] : [0xCD, 0x80];

        foreach (var map in ParseMaps(Id))
        {
            if (!map.Executable || map.Length <= 0)
                continue;

            var scanLength = (int)Math.Min(map.Length, 8 * 1024 * 1024);
            byte[] data;
            try
            {
                data = Read(map.Start, scanLength);
            }
            catch
            {
                continue;
            }

            var index = IndexOf(data, needle);
            if (index >= 0)
                return map.Start + index;
        }

        throw new InvalidOperationException("Could not find a syscall gadget in the client process.");
    }

    private unsafe ulong InjectSyscall64(int tid, nint gadget, ulong rax, ulong rdi, ulong rsi, ulong rdx, ulong r10, ulong r8, ulong r9)
    {
        var regs = new Native.UserRegs64();
        if (Native.ptrace(Native.PTRACE_GETREGS, tid, 0, (nint)(&regs)) != 0)
            throw PtraceFailed("PTRACE_GETREGS");

        var saved = regs;
        regs.rax = rax;
        regs.rdi = rdi;
        regs.rsi = rsi;
        regs.rdx = rdx;
        regs.r10 = r10;
        regs.r8 = r8;
        regs.r9 = r9;
        regs.rip = (ulong)gadget;

        if (Native.ptrace(Native.PTRACE_SETREGS, tid, 0, (nint)(&regs)) != 0)
            throw PtraceFailed("PTRACE_SETREGS");

        try
        {
            if (Native.ptrace(Native.PTRACE_SINGLESTEP, tid, 0, 0) != 0)
                throw PtraceFailed("PTRACE_SINGLESTEP");
            if (Native.waitpid(tid, out _, 0) < 0)
                throw PtraceFailed("waitpid");

            if (Native.ptrace(Native.PTRACE_GETREGS, tid, 0, (nint)(&regs)) != 0)
                throw PtraceFailed("PTRACE_GETREGS");

            return regs.rax;
        }
        finally
        {
            Native.ptrace(Native.PTRACE_SETREGS, tid, 0, (nint)(&saved));
        }
    }

    private unsafe int InjectSyscall32Mmap2(int tid, nint gadget, int length)
    {
        var regs = new Native.UserRegs32();
        if (Native.ptrace(Native.PTRACE_GETREGS, tid, 0, (nint)(&regs)) != 0)
            throw PtraceFailed("PTRACE_GETREGS");

        var saved = regs;
        regs.eax = SysMmap2;
        regs.ebx = 0;
        regs.ecx = (uint)length;
        regs.edx = ProtReadWriteExec;
        regs.esi = MapPrivateAnonymous;
        regs.edi = unchecked((uint)-1);
        regs.ebp = 0;
        regs.eip = (uint)gadget;

        if (Native.ptrace(Native.PTRACE_SETREGS, tid, 0, (nint)(&regs)) != 0)
            throw PtraceFailed("PTRACE_SETREGS");

        try
        {
            if (Native.ptrace(Native.PTRACE_SINGLESTEP, tid, 0, 0) != 0)
                throw PtraceFailed("PTRACE_SINGLESTEP");
            if (Native.waitpid(tid, out _, 0) < 0)
                throw PtraceFailed("waitpid");

            if (Native.ptrace(Native.PTRACE_GETREGS, tid, 0, (nint)(&regs)) != 0)
                throw PtraceFailed("PTRACE_GETREGS");

            return (int)regs.eax;
        }
        finally
        {
            Native.ptrace(Native.PTRACE_SETREGS, tid, 0, (nint)(&saved));
        }
    }

    private unsafe bool TryProcessVm(bool write, nint remoteAddress, ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length == 0)
            return true;

        fixed (byte* local = buffer)
        {
            var localIov = new Native.Iovec { iov_base = (nint)local, iov_len = (nuint)buffer.Length };
            var remoteIov = new Native.Iovec { iov_base = remoteAddress, iov_len = (nuint)buffer.Length };
            var n = write
                ? Native.process_vm_writev(Id, &localIov, 1, &remoteIov, 1, 0)
                : Native.process_vm_readv(Id, &localIov, 1, &remoteIov, 1, 0);
            return n == buffer.Length;
        }
    }

    private bool TryWrite(nint address, ReadOnlySpan<byte> data)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            var chunk = Math.Min(PageSize, data.Length - offset);
            if (!TryProcessVm(write: true, address + offset, data.Slice(offset, chunk)))
                return false;
            offset += chunk;
        }

        return true;
    }

    private void Unprotect(nint address, int length)
    {
        var start = (long)address & ~(PageSize - 1);
        var end = ((long)address + length + PageSize - 1) & ~(PageSize - 1);
        var size = end - start;
        var gadget = EnsureSyscallGadget();
        var tid = _attachedTids[0];

        long result = _is64Bit
            ? (long)InjectSyscall64(tid, gadget, SysMprotect, (ulong)start, (ulong)size, ProtReadWriteExec, 0, 0, 0)
            : InjectSyscall32(tid, gadget, SysMprotect32, (uint)start, (uint)size, ProtReadWriteExec);

        if (result is < 0 and > -4096)
            throw new InvalidOperationException($"mprotect in the client process failed (errno {-result}).");
    }

    private unsafe int InjectSyscall32(int tid, nint gadget, uint eax, uint ebx, uint ecx, uint edx)
    {
        var regs = new Native.UserRegs32();
        if (Native.ptrace(Native.PTRACE_GETREGS, tid, 0, (nint)(&regs)) != 0)
            throw PtraceFailed("PTRACE_GETREGS");

        var saved = regs;
        regs.eax = eax;
        regs.ebx = ebx;
        regs.ecx = ecx;
        regs.edx = edx;
        regs.eip = (uint)gadget;

        if (Native.ptrace(Native.PTRACE_SETREGS, tid, 0, (nint)(&regs)) != 0)
            throw PtraceFailed("PTRACE_SETREGS");

        try
        {
            if (Native.ptrace(Native.PTRACE_SINGLESTEP, tid, 0, 0) != 0)
                throw PtraceFailed("PTRACE_SINGLESTEP");
            if (Native.waitpid(tid, out _, 0) < 0)
                throw PtraceFailed("waitpid");

            if (Native.ptrace(Native.PTRACE_GETREGS, tid, 0, (nint)(&regs)) != 0)
                throw PtraceFailed("PTRACE_GETREGS");

            return (int)regs.eax;
        }
        finally
        {
            Native.ptrace(Native.PTRACE_SETREGS, tid, 0, (nint)(&saved));
        }
    }

    private void WriteViaPtrace(nint address, ReadOnlySpan<byte> data)
    {
        var tid = _attachedTids[0];
        var wordSize = _is64Bit ? 8 : 4;
        var start = (long)address;
        var end = start + data.Length;
        var aligned = start & ~(wordSize - 1);

        for (var addr = aligned; addr < end; addr += wordSize)
        {
            var wordBytes = new byte[wordSize];
            var needsPeek = addr < start || addr + wordSize > end;
            if (needsPeek)
            {
                Marshal.SetLastPInvokeError(0);
                var peeked = Native.ptrace(Native.PTRACE_PEEKDATA, tid, (nint)addr, 0);
                if (peeked == -1 && Marshal.GetLastPInvokeError() != 0)
                    throw PtraceFailed("PTRACE_PEEKDATA");

                var peekValue = _is64Bit ? (ulong)peeked : (uint)peeked;
                BitConverter.GetBytes(peekValue).AsSpan(0, wordSize).CopyTo(wordBytes);
            }

            for (var i = 0; i < wordSize; i++)
            {
                var sourceIndex = addr + i - start;
                if (sourceIndex >= 0 && sourceIndex < data.Length)
                    wordBytes[i] = data[(int)sourceIndex];
            }

            nint poke = _is64Bit
                ? (nint)BitConverter.ToInt64(wordBytes, 0)
                : (nint)BitConverter.ToUInt32(wordBytes, 0);

            if (Native.ptrace(Native.PTRACE_POKEDATA, tid, (nint)addr, poke) != 0)
                throw PtraceFailed("PTRACE_POKEDATA");
        }
    }

    private void ReadProcMem(nint address, Span<byte> buffer)
    {
        using var fs = new FileStream($"/proc/{Id}/mem", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek((long)address, SeekOrigin.Begin);
        fs.ReadExactly(buffer);
    }

    private static bool IsElf64(int pid)
    {
        try
        {
            using var fs = File.OpenRead($"/proc/{pid}/exe");
            var ident = new byte[5];
            fs.ReadExactly(ident);
            if (ident[0] == 0x7F && ident[1] == (byte)'E' && ident[2] == (byte)'L' && ident[3] == (byte)'F')
                return ident[4] == 2;
        }
        catch
        {
            // Fall through to maps inspection.
        }

        return ParseMaps(pid).Any(m => (ulong)m.Start >= 0x1_0000_0000UL);
    }

    private static IEnumerable<int> EnumeratePids()
    {
        if (!Directory.Exists("/proc"))
            yield break;

        foreach (var dir in Directory.GetDirectories("/proc"))
        {
            if (int.TryParse(Path.GetFileName(dir), out var pid))
                yield return pid;
        }
    }

    private static IEnumerable<int> EnumerateDescendantsAndSelf(int root)
    {
        var stack = new Stack<int>();
        stack.Push(root);
        var seen = new HashSet<int>();

        while (stack.Count > 0)
        {
            var pid = stack.Pop();
            if (!seen.Add(pid))
                continue;

            yield return pid;

            var taskDir = $"/proc/{pid}/task";
            if (!Directory.Exists(taskDir))
                continue;

            foreach (var tidDir in Directory.GetDirectories(taskDir))
            {
                var childrenPath = Path.Combine(tidDir, "children");
                if (!File.Exists(childrenPath))
                    continue;

                string text;
                try
                {
                    text = File.ReadAllText(childrenPath);
                }
                catch
                {
                    continue;
                }

                foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (int.TryParse(token, out var child))
                        stack.Push(child);
                }
            }
        }
    }

    private static bool MapsContain(int pid, string exePath, string fileName)
    {
        try
        {
            foreach (var map in ParseMaps(pid))
            {
                if (string.IsNullOrEmpty(map.Path))
                    continue;

                var mapFileName = Path.GetFileName(map.Path.Replace('\\', '/'));
                if (map.Path.Equals(exePath, StringComparison.OrdinalIgnoreCase)
                    || mapFileName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static List<MapRange> ParseMaps(int pid)
    {
        var maps = new List<MapRange>();
        foreach (var line in File.ReadLines($"/proc/{pid}/maps"))
        {
            var space = line.IndexOf(' ');
            if (space < 0)
                continue;

            var range = line[..space];
            var dash = range.IndexOf('-');
            if (dash < 0)
                continue;

            if (!ulong.TryParse(range[..dash], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var start)
                || !ulong.TryParse(range[(dash + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var end))
                continue;

            var rest = line[(space + 1)..];
            if (rest.Length < 4)
                continue;

            var perms = rest[..4];
            var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            ulong offset = 0;
            if (parts.Length > 1)
                ulong.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out offset);

            var path = parts.Length > 4 ? string.Join(' ', parts.Skip(4)) : string.Empty;
            maps.Add(new MapRange((nint)start, (long)(end - start), perms.Contains('x'), (long)offset, path));
        }

        return maps;
    }

    private static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return i;
        }

        return -1;
    }

    private static InvalidOperationException PtraceFailed(string operation) =>
        new($"{operation} failed: {Marshal.GetLastPInvokeErrorMessage()}");

    private readonly record struct MapRange(nint Start, long Length, bool Executable, long FileOffset, string Path);

    private static class Native
    {
        internal const int PTRACE_PEEKDATA = 2;
        internal const int PTRACE_POKEDATA = 5;
        internal const int PTRACE_CONT = 7;
        internal const int PTRACE_SINGLESTEP = 9;
        internal const int PTRACE_GETREGS = 12;
        internal const int PTRACE_SETREGS = 13;
        internal const int PTRACE_ATTACH = 16;
        internal const int PTRACE_DETACH = 17;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Iovec
        {
            public nint iov_base;
            public nuint iov_len;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct UserRegs64
        {
            public ulong r15, r14, r13, r12, rbp, rbx, r11, r10, r9, r8;
            public ulong rax, rcx, rdx, rsi, rdi, orig_rax, rip, cs, eflags, rsp, ss;
            public ulong fs_base, gs_base, ds, es, fs, gs;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct UserRegs32
        {
            public uint ebx, ecx, edx, esi, edi, ebp, eax;
            public ushort xds, xdsPad, xes, xesPad, xfs, xfsPad, xgs, xgsPad;
            public uint orig_eax, eip;
            public ushort xcs, xcsPad;
            public uint eflags, esp;
            public ushort xss, xssPad;
        }

        [DllImport("libc", SetLastError = true)]
        internal static extern unsafe nint process_vm_readv(int pid, Iovec* localIov, nuint liovcnt, Iovec* remoteIov, nuint riovcnt, nuint flags);

        [DllImport("libc", SetLastError = true)]
        internal static extern unsafe nint process_vm_writev(int pid, Iovec* localIov, nuint liovcnt, Iovec* remoteIov, nuint riovcnt, nuint flags);

        [DllImport("libc", SetLastError = true)]
        internal static extern nint ptrace(int request, int pid, nint addr, nint data);

        [DllImport("libc", SetLastError = true)]
        internal static extern int waitpid(int pid, out int status, int options);

        [DllImport("libc", SetLastError = true)]
        internal static extern int kill(int pid, int sig);
    }
}
