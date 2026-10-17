using System;
using System.Collections.Generic;
using Tzar2.Formats.Text;

namespace Tzar2.Formats.Hpfs;

/// <summary>One file or directory of an HPFS container.</summary>
/// <param name="Path">Path from the root with <c>/</c> separators; directories end with <c>/</c>.</param>
/// <param name="IsDirectory">Whether this is a directory.</param>
/// <param name="Inode">The inode block.</param>
/// <param name="Data">File content (empty for directories).</param>
public sealed record HpfsEntry(string Path, bool IsDirectory, int Inode, byte[] Data)
{
    /// <summary>Whether the content is itself an HPFS image (maps embedded in campaigns).</summary>
    public bool IsNestedArchive => !IsDirectory && HpfsArchive.IsHpfs(Data);
}

/// <summary>
/// Reader for HPFS block containers (<c>.wmp</c> maps, <c>.wcm</c> campaigns; docs/formats/hpfs.md): 512-byte blocks,
/// inode blocks with direct (level 0) or indirect pointers, packed directory entries, root inode at block 1.
/// Truncated last blocks are zero-padded. Every block may be referenced at most once, which also rejects cycles.
/// </summary>
public sealed class HpfsArchive
{
    /// <summary>The only block size seen.</summary>
    public const int BlockSize = 512;

    private const string Format = "HPFS";
    private const int RootInode = 1;
    private const int MaxLevel = 3;
    private const int MaxDepth = 64;
    private const int PointersPerBlock = BlockSize / 4;
    private const int DirectPointers = (BlockSize - 8) / 4;

    private readonly byte[] _data;
    private readonly bool[] _used;

    private HpfsArchive(byte[] data, int blockCount, int freeBlocks, int originalLength, CodePage codePage)
    {
        _data = data;
        BlockCount = blockCount;
        FreeBlockCount = freeBlocks;
        _used = new bool[blockCount];
        _used[0] = true;
        TruncatedBytes = (blockCount * BlockSize) - originalLength;
        NameCodePage = codePage;
    }

    /// <summary>Total blocks including the header block.</summary>
    public int BlockCount { get; }

    /// <summary>Free-block count from the header (informational).</summary>
    public int FreeBlockCount { get; }

    /// <summary>How many bytes of the last block were missing (campaigns are truncated) and were zero-filled.</summary>
    public int TruncatedBytes { get; }

    /// <summary>Code page used to decode member names.</summary>
    public CodePage NameCodePage { get; }

    /// <summary>Number of blocks referenced so far (after <see cref="ReadAll"/>: all reachable blocks incl. the header).</summary>
    public int ReferencedBlockCount
    {
        get
        {
            int n = 0;
            foreach (bool used in _used)
            {
                n += used ? 1 : 0;
            }

            return n;
        }
    }

    /// <summary>Whether <paramref name="data"/> starts with the HPFS signature.</summary>
    public static bool IsHpfs(ReadOnlySpan<byte> data) => ByteReader.Matches(data, 0, "HPFS"u8);

    /// <summary>Opens an HPFS image and validates its header.</summary>
    /// <exception cref="LegacyFormatException">The data is malformed.</exception>
    public static HpfsArchive Open(ReadOnlySpan<byte> data, CodePage nameCodePage = CodePage.Windows1252)
    {
        if (!IsHpfs(data))
        {
            throw new LegacyFormatException(Format, "missing 'HPFS' signature.");
        }

        uint blockSize = ByteReader.U32(data, 4, Format, "block size");
        uint blockCount = ByteReader.U32(data, 8, Format, "block count");
        uint free = ByteReader.U32(data, 0x10, Format, "free count");
        if (blockSize != BlockSize)
        {
            throw new LegacyFormatException(Format, $"block size {blockSize} (expected 512).");
        }

        long full = (long)blockCount * BlockSize;
        if (blockCount < 2 || !((long)(blockCount - 1) * BlockSize < data.Length && data.Length <= full))
        {
            throw new LegacyFormatException(Format, $"{blockCount} blocks do not match the data length {data.Length}.");
        }

        byte[] padded = new byte[full];
        data.CopyTo(padded);
        return new HpfsArchive(padded, (int)blockCount, (int)Math.Min(free, int.MaxValue), data.Length, nameCodePage);
    }

    /// <summary>Reads every entry depth-first in directory order (each directory before its children).</summary>
    /// <exception cref="LegacyFormatException">The tree is malformed.</exception>
    public IReadOnlyList<HpfsEntry> ReadAll()
    {
        Array.Clear(_used);
        _used[0] = true;
        var entries = new List<HpfsEntry>();
        Walk(RootInode, string.Empty, entries, 0);
        return entries;
    }

    private void Walk(int inode, string prefix, List<HpfsEntry> entries, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new LegacyFormatException(Format, "directory tree too deep.");
        }

        byte[] dir = ReadInode(inode);
        int o = 0;
        while (o < dir.Length)
        {
            if (o + 8 > dir.Length)
            {
                throw new LegacyFormatException(Format, $"truncated directory entry in '{prefix}'.");
            }

            uint child = BitConverterLe.U32(dir, o);
            ushort kind = BitConverterLe.U16(dir, o + 4);
            ushort nameLength = BitConverterLe.U16(dir, o + 6);
            if (o + 8 + nameLength > dir.Length)
            {
                throw new LegacyFormatException(Format, $"directory entry name runs past the directory in '{prefix}'.");
            }

            string name = LegacyText.Decode(dir.AsSpan(o + 8, nameLength), NameCodePage);
            o += 8 + nameLength;
            if (child == 0 || child >= BlockCount)
            {
                throw new LegacyFormatException(Format, $"entry '{prefix}{name}' points to block {child} out of range.");
            }

            string path = prefix + name;
            switch (kind)
            {
                case 1:
                    entries.Add(new HpfsEntry(path + "/", true, (int)child, []));
                    Walk((int)child, path + "/", entries, depth + 1);
                    break;
                case 0:
                    entries.Add(new HpfsEntry(path, false, (int)child, ReadInode((int)child)));
                    break;
                default:
                    throw new LegacyFormatException(Format, $"unknown entry kind {kind} for '{path}'.");
            }
        }
    }

    private byte[] ReadInode(int inode)
    {
        Claim(inode);
        int blockOffset = inode * BlockSize;
        uint size = BitConverterLe.U32(_data, blockOffset);
        uint level = BitConverterLe.U32(_data, blockOffset + 4);
        if (size > (long)BlockCount * BlockSize)
        {
            throw new LegacyFormatException(Format, $"inode {inode}: size {size} exceeds the container.");
        }

        if (level > MaxLevel)
        {
            throw new LegacyFormatException(Format, $"inode {inode}: indirection level {level} unsupported.");
        }

        int blocks = (int)((size + BlockSize - 1) / BlockSize);
        var pointers = new List<uint>(DirectPointers);
        for (int i = 0; i < DirectPointers; i++)
        {
            pointers.Add(BitConverterLe.U32(_data, blockOffset + 8 + (4 * i)));
        }

        for (int remaining = (int)level; remaining > 0; remaining--)
        {
            long need = blocks;
            for (int l = 0; l < remaining; l++)
            {
                need = (need + PointersPerBlock - 1) / PointersPerBlock;
            }

            if (need > pointers.Count)
            {
                throw new LegacyFormatException(Format, $"inode {inode}: not enough index pointers.");
            }

            var next = new List<uint>((int)need * PointersPerBlock);
            for (int i = 0; i < need; i++)
            {
                int index = CheckedBlock(pointers[i], inode);
                Claim(index);
                for (int k = 0; k < PointersPerBlock; k++)
                {
                    next.Add(BitConverterLe.U32(_data, (index * BlockSize) + (4 * k)));
                }
            }

            pointers = next;
        }

        if (pointers.Count < blocks)
        {
            throw new LegacyFormatException(Format, $"inode {inode}: not enough block pointers.");
        }

        byte[] content = new byte[size];
        for (int i = 0; i < blocks; i++)
        {
            int index = CheckedBlock(pointers[i], inode);
            Claim(index);
            int length = (int)Math.Min(BlockSize, size - ((long)i * BlockSize));
            Array.Copy(_data, index * BlockSize, content, i * BlockSize, length);
        }

        return content;
    }

    private int CheckedBlock(uint block, int inode)
    {
        if (block == 0 || block >= BlockCount)
        {
            throw new LegacyFormatException(Format, $"inode {inode}: block {block} out of range.");
        }

        return (int)block;
    }

    private void Claim(int block)
    {
        if (block <= 0 || block >= BlockCount)
        {
            throw new LegacyFormatException(Format, $"block {block} out of range.");
        }

        if (_used[block])
        {
            throw new LegacyFormatException(Format, $"block {block} is referenced twice.");
        }

        _used[block] = true;
    }

    private static class BitConverterLe
    {
        public static uint U32(byte[] data, int offset) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));

        public static ushort U16(byte[] data, int offset) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
    }
}
