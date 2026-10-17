"""Prototype reader for Tzar HPFS containers (.wmp maps, .wcm campaigns); see docs/formats/hpfs.md.

Usage: python3 -I hpfs.py <file> [--extract <dir>] [--recurse]
  --recurse also opens embedded *.wmp files (campaigns contain whole maps).
"""
import os
import struct
import sys


class HpfsError(ValueError):
    pass


class Hpfs:
    ROOT = 1

    def __init__(self, data):
        if data[:4] != b'HPFS':
            raise HpfsError('bad magic')
        (self.block_size, self.block_count, self.h0c, self.h10, self.h14,
         self.h18, self.h1c) = struct.unpack_from('<IIiIIII', data, 4)
        if self.block_size != 512 or not (self.block_count - 1) * 512 < len(data) <= self.block_count * 512:
            raise HpfsError('block geometry does not match file size')
        # Campaign files (.wcm) and maps embedded in them omit the tail of their last block.
        self.d = data + bytes(self.block_count * 512 - len(data))
        self.ptrs_per_block = self.block_size // 4
        self.used = {0: 'header'}

    def _block(self, b):
        if not 0 < b < self.block_count:
            raise HpfsError('block %d out of range' % b)
        return self.d[b * self.block_size:(b + 1) * self.block_size]

    def _claim(self, b, owner):
        if b in self.used:
            raise HpfsError('block %d used by %s and %s' % (b, self.used[b], owner))
        self.used[b] = owner

    def read_inode(self, ino, owner='?'):
        blk = self._block(ino)
        self._claim(ino, owner)
        size, level = struct.unpack_from('<II', blk, 0)
        nblocks = (size + self.block_size - 1) // self.block_size
        direct = (self.block_size - 8) // 4
        ptrs = list(struct.unpack_from('<%dI' % direct, blk, 8))
        # level 0: pointers are data blocks; level n: pointers are index blocks
        # (128 uint32 each) n levels deep.
        for _ in range(level):
            fan = self.ptrs_per_block
            need = nblocks
            for _ in range(level - 1):
                need = (need + fan - 1) // fan
            need = (need + fan - 1) // fan
            nxt = []
            for p in ptrs[:need]:
                self._claim(p, owner + ' (index)')
                nxt += struct.unpack_from('<%dI' % fan, self._block(p), 0)
            ptrs = nxt
            level -= 1
        if len(ptrs) < nblocks:
            raise HpfsError('inode %d: not enough block pointers' % ino)
        out = bytearray()
        for p in ptrs[:nblocks]:
            self._claim(p, owner)
            out += self._block(p)
        return bytes(out[:size])

    def walk(self, ino=ROOT, prefix=''):
        """Yield (path, is_dir, inode, data-or-None)."""
        data = self.read_inode(ino, prefix or '/')
        o = 0
        while o < len(data):
            if o + 8 > len(data):
                raise HpfsError('truncated directory entry in %s' % (prefix or '/'))
            child, kind, nlen = struct.unpack_from('<IHH', data, o)
            name = data[o + 8:o + 8 + nlen].decode('cp1252')
            o += 8 + nlen
            path = prefix + name
            if kind == 1:
                yield path + '/', True, child, None
                yield from self.walk(child, path + '/')
            elif kind == 0:
                yield path, False, child, self.read_inode(child, path)
            else:
                raise HpfsError('unknown entry kind %d for %s' % (kind, path))


def main(argv):
    path = argv[1]
    out = argv[argv.index('--extract') + 1] if '--extract' in argv else None
    recurse = '--recurse' in argv
    with open(path, 'rb') as f:
        data = f.read()

    def dump(blob, label, depth):
        fs = Hpfs(blob)
        files = 0
        for p, is_dir, ino, payload in fs.walk():
            if is_dir:
                continue
            files += 1
            print('%s%-50s %9d' % ('  ' * depth, p, len(payload)))
            if out:
                dst = os.path.join(out, label, *p.split('/'))
                os.makedirs(os.path.dirname(dst), exist_ok=True)
                with open(dst, 'wb') as fh:
                    fh.write(payload)
            if recurse and payload[:4] == b'HPFS':
                dump(payload, os.path.join(label, p.replace('/', '_')), depth + 1)
        free = fs.block_count - len(fs.used)
        print('%s-- %s: %d files, %d/%d blocks referenced, %d unreferenced' %
              ('  ' * depth, label, files, len(fs.used), fs.block_count, free))

    dump(data, os.path.basename(path), 0)


if __name__ == '__main__':
    main(sys.argv)
