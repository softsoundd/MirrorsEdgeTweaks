using System.Buffers.Binary;
using System.Text;

namespace MirrorsEdgeTweaks.Helpers
{
    public sealed class CaveSection
    {
        internal const int HeaderSize = 12;
        private const int InitialAlloc = HeaderSize;
        private const int PageSize = 0x1000;
        private const uint SectionChars = 0xE0000020;
        private const uint ExecuteFlag = 0x20000000, WriteFlag = 0x80000000;
        private static readonly byte[] SectionName = Encoding.ASCII.GetBytes(".cave\0\0\0");
        private static readonly HashSet<string> KnownTags =
            new() { "gog", "steam", "retail", "retold", "dlc", "ea" };

        private byte[] _peData = Array.Empty<byte>();
        private uint _imageBase;
        private uint _fileAlign;
        private uint _sectionAlign;

        private bool _sectionExists;
        private int _sectionHdrOffset;
        private uint _sectionVa;
        private int _sectionRawOffset;
        private int _sectionRawSize;
        private int _sectionVirtSize;

        private int _watermark = InitialAlloc;
        private string _versionTag = "";
        private bool _secondary;
        private bool _usingPadding;
        private uint _paddingFlags;

        private int _peOffset;
        private int _coffOffset;
        private int _optOffset;
        private int _numSections;
        private int _sectionsOffset;

        private bool _reuse;
        private int _reuseCursor;

        private readonly List<(int SectionOffset, byte[] Data)> _pending = new();

        private CaveSection() { }

        public static CaveSection Open(byte[] peData, string versionTag = "") => OpenCave(peData, versionTag, false);

        // Opens the cave for patches that must leave the .text padding to the core patches.
        // Returns the same .cave section as Open when the image has or can add one.
        // Returns a cave in the zero tail of .rdata otherwise, and Finalize makes .rdata executable and writable.
        public static CaveSection OpenSecondary(byte[] peData, string versionTag = "") => OpenCave(peData, versionTag, true);

        private static CaveSection OpenCave(byte[] peData, string versionTag, bool secondary)
        {
            var cs = new CaveSection
            {
                _peData = peData,
                _versionTag = versionTag,
                _secondary = secondary
            };
            cs.ParsePe();
            return cs;
        }

        public uint Alloc(int size, int align = 4)
        {
            EnsureSection();

            // A patch that reapplies over its own block allocates from a private cursor inside that block.
            // The shared watermark stays as it is, so the patch only overwrites its own region and never another patch's.
            if (_reuse)
            {
                int roff = (_reuseCursor + align - 1) & ~(align - 1);
                int rend = roff + size;
                if (rend > _watermark)
                    throw new InvalidOperationException(
                        $"Reuse allocation (end {rend}) exceeds the existing cave block (watermark {_watermark}).");
                _reuseCursor = rend;
                return (uint)(_sectionVa + roff);
            }

            int off = (_watermark + align - 1) & ~(align - 1);
            int end = off + size;
            GrowIfNeeded(end);
            _watermark = end;
            return (uint)(_sectionVa + off);
        }

        // Frees a patch's cave block when the block ends exactly at the watermark of the cave that holds it.
        // Returns the data unchanged otherwise, so a block above it is never zeroed.
        public static byte[] ReclaimIfTopmost(byte[] peData, uint caveBaseVa, int footprintSize)
        {
            var cave = Open(peData);
            if (!cave.Holds(caveBaseVa))
                cave = OpenSecondary(peData);
            int baseOff = (int)(caveBaseVa - cave.SectionVa);
            if (baseOff >= InitialAlloc && baseOff + footprintSize == cave.CurrentWatermark)
            {
                cave.RewindTo(baseOff);
                return cave.Finalize();
            }
            return peData;
        }

        private bool Holds(uint va)
        {
            EnsureSection();
            return va - _sectionVa < (uint)_sectionRawSize;
        }

        // Makes later Alloc calls overlay an existing block from the section offset `offset`, instead of appending at the watermark.
        // A patch that reapplies its own deterministic block passes its previous base, so it rewrites the block in place.
        public void ReuseFrom(int offset)
        {
            EnsureSection();
            if (offset < InitialAlloc || offset > _watermark)
                throw new InvalidOperationException(
                    $"ReuseFrom offset {offset} outside [{InitialAlloc}..{_watermark}].");
            _reuse = true;
            _reuseCursor = offset;
        }

        // Returns the section-relative offset of the first match at or after fromOffset.
        // Returns -1 when the allocated part of the cave has no match.
        // Pending writes are not searched, because they only reach the image in Finalize.
        public int IndexOf(ReadOnlySpan<byte> pattern, int fromOffset = 0)
        {
            EnsureSection();
            int start = Math.Max(fromOffset, InitialAlloc);
            if (start >= _watermark) return -1;
            int idx = _peData.AsSpan(_sectionRawOffset + start, _watermark - start).IndexOf(pattern);
            return idx < 0 ? -1 : start + idx;
        }

        public void Write(uint va, byte[] data)
        {
            int secOff = (int)(va - _sectionVa);
            if (secOff < HeaderSize || secOff + data.Length > _sectionRawSize)
                throw new InvalidOperationException(
                    $"Cave write at VA 0x{va:X8} (section offset {secOff}) out of bounds [{HeaderSize}..{_sectionRawSize}).");
            _pending.Add((secOff, data));
        }

        public byte[] Finalize()
        {
            EnsureSection();
            CommitSection();
            if (_usingPadding)
            {
                uint chars = BinaryPrimitives.ReadUInt32LittleEndian(_peData.AsSpan(_sectionHdrOffset + 36, 4));
                BinaryPrimitives.WriteUInt32LittleEndian(_peData.AsSpan(_sectionHdrOffset + 36, 4), chars | _paddingFlags);
            }
            foreach (var (secOff, data) in _pending)
            {
                int foff = _sectionRawOffset + secOff;
                Buffer.BlockCopy(data, 0, _peData, foff, data.Length);
            }
            WriteWatermark();
            UpdateSizeOfImage();
            return _peData;
        }

        public uint SectionVa
        {
            get
            {
                EnsureSection();
                return _sectionVa;
            }
        }

        public int CurrentWatermark
        {
            get
            {
                EnsureSection();
                return _watermark;
            }
        }

        // Returns the fixed size of a padding cave.
        // Returns int.MaxValue for a growable .cave section.
        public int Capacity
        {
            get
            {
                EnsureSection();
                return _usingPadding ? _sectionRawSize : int.MaxValue;
            }
        }

        // Frees cave space down to the section offset `offset` and zero-fills the freed range,
        // so a later apply reuses the space instead of growing the cave.
        public void RewindTo(int offset)
        {
            EnsureSection();
            if (offset < InitialAlloc || offset > _watermark)
                throw new InvalidOperationException(
                    $"RewindTo offset {offset} outside [{InitialAlloc}..{_watermark}].");
            if (offset == _watermark) return;
            _pending.Add((offset, new byte[_watermark - offset]));
            _watermark = offset;
        }

        private void ParsePe()
        {
            var pe = PeImageLayout.Parse(_peData);

            _peOffset = pe.PeOffset;
            _coffOffset = pe.CoffOffset;
            _optOffset = pe.OptionalHeaderOffset;
            _sectionsOffset = pe.SectionTableOffset;
            _numSections = pe.Sections.Count;
            _imageBase = pe.ImageBase;
            _sectionAlign = pe.SectionAlignment;
            _fileAlign = pe.FileAlignment;

            foreach (var section in pe.Sections)
            {
                if (section.Name == ".cave")
                {
                    LoadExisting(section.HeaderOffset);
                    return;
                }
            }
        }

        private void LoadExisting(int hdrOff)
        {
            var d = _peData;
            _sectionExists = true;
            _sectionHdrOffset = hdrOff;

            _sectionVirtSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(hdrOff + 8, 4));
            uint rva = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(hdrOff + 12, 4));
            _sectionRawSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(hdrOff + 16, 4));
            _sectionRawOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(hdrOff + 20, 4));

            _sectionVa = _imageBase + rva;

            _watermark = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(_sectionRawOffset, 4));
            if (_watermark < InitialAlloc)
                _watermark = InitialAlloc;
            _versionTag = ReadTag(d, _sectionRawOffset);
        }

        private static string ReadTag(byte[] d, int headerOffset)
        {
            ReadOnlySpan<byte> tag = d.AsSpan(headerOffset + 4, 8);
            int nul = tag.IndexOf((byte)0);
            return Encoding.ASCII.GetString(nul >= 0 ? tag[..nul] : tag);
        }

        private void EnsureSection()
        {
            if (_sectionExists) return;
            if (CanAddSection())
                CreateSection();
            else if (_secondary)
                UsePadding(".rdata", ExecuteFlag | WriteFlag);
            else
                UsePadding(".text", WriteFlag);
        }

        private bool CanAddSection()
        {
            int newHdrOff = _sectionsOffset + _numSections * 40;
            int headersEnd = newHdrOff + 40;
            uint sizeOfHeaders = BinaryPrimitives.ReadUInt32LittleEndian(_peData.AsSpan(_optOffset + 60, 4));
            return headersEnd <= sizeOfHeaders;
        }

        // A padding cave takes the zero bytes past the section's virtual size, from a 16-byte boundary.
        // Finalize gives the section the flags, so opening a cave leaves the image unchanged.
        private void UsePadding(string name, uint flags)
        {
            var d = _peData;
            int hdr = -1;
            for (int i = 0; i < _numSections && hdr < 0; i++)
            {
                int candidate = _sectionsOffset + i * 40;
                int nameEnd = 8;
                while (nameEnd > 0 && d[candidate + nameEnd - 1] == 0) nameEnd--;
                if (Encoding.ASCII.GetString(d, candidate, nameEnd) == name)
                    hdr = candidate;
            }

            if (hdr < 0)
                throw new InvalidOperationException($"No {name} section found for a padding cave.");

            uint virtSize = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(hdr + 8, 4));
            uint rva = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(hdr + 12, 4));
            int rawSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(hdr + 16, 4));
            int raw = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(hdr + 20, 4));
            int end = raw + rawSize;
            int floor = AlignUp(raw + (int)Math.Min(virtSize, (uint)rawSize), 16);

            int nulEndScan = end;
            while (nulEndScan > floor && d[nulEndScan - 1] == 0)
                nulEndScan--;
            int start = AlignUp(nulEndScan, 16);

            int? existing = FindPaddingCaveHeader(d, floor, end);
            if (existing.HasValue)
            {
                start = existing.Value;
                _watermark = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(start, 4));
                if (_watermark < InitialAlloc)
                    _watermark = InitialAlloc;
                _versionTag = ReadTag(d, start);
            }
            else
            {
                if (end - start < 256)
                    throw new InvalidOperationException(
                        $"Only {Math.Max(end - start, 0)} bytes of NUL padding in {name} (need at least 256).");
                _watermark = InitialAlloc;
            }

            _sectionExists = true;
            _sectionHdrOffset = hdr;
            _sectionVa = _imageBase + rva + (uint)(start - raw);
            _sectionRawOffset = start;
            _sectionRawSize = end - start;
            _sectionVirtSize = end - start;
            _usingPadding = true;
            _paddingFlags = flags;
        }

        private static int? FindPaddingCaveHeader(byte[] d, int floor, int end)
        {
            int limit = Math.Max(floor, end - 0x2000);
            for (int off = end - 16; off >= limit; off -= 16)
            {
                if (off + 12 > d.Length) continue;
                uint wm = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off, 4));

                // A watermark of HeaderSize marks a reclaimed, empty cave.
                // It must still be found, so the next apply reuses this location instead of starting past the leftover header.
                if (wm < HeaderSize || wm > end - off) continue;

                if (KnownTags.Contains(ReadTag(d, off)))
                    return off;
            }
            return null;
        }

        private void CreateSection()
        {
            var d = _peData;

            int lastHdr = _sectionsOffset + (_numSections - 1) * 40;
            uint lastRva = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(lastHdr + 12, 4));
            uint lastVirt = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(lastHdr + 8, 4));
            int lastRawOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(lastHdr + 20, 4));
            int lastRawSz = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(lastHdr + 16, 4));

            uint newRva = AlignUp(lastRva + lastVirt, _sectionAlign);
            int newRawOff = AlignUp(lastRawOff + lastRawSz, (int)_fileAlign);
            int newRawSize = PageSize;
            int newVirtSize = PageSize;

            int newHdrOff = _sectionsOffset + _numSections * 40;

            // Signed builds (Steam) keep their certificate where the new section goes, so it is left out of the copy.
            d = new byte[newRawOff + newRawSize];
            Buffer.BlockCopy(_peData, 0, d, 0, Math.Min(_peData.Length, newRawOff));
            PeImageLayout.ClearCertificateEntry(d);
            _peData = d;

            var hdr = new byte[40];
            Buffer.BlockCopy(SectionName, 0, hdr, 0, 8);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(8, 4), (uint)newVirtSize);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(12, 4), newRva);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(16, 4), (uint)newRawSize);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(20, 4), (uint)newRawOff);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(36, 4), SectionChars);
            Buffer.BlockCopy(hdr, 0, d, newHdrOff, 40);

            BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(_coffOffset + 2, 2), (ushort)(_numSections + 1));

            _numSections++;
            _sectionExists = true;
            _sectionHdrOffset = newHdrOff;
            _sectionVa = _imageBase + newRva;
            _sectionRawOffset = newRawOff;
            _sectionRawSize = newRawSize;
            _sectionVirtSize = newVirtSize;
            _watermark = InitialAlloc;
        }

        private void GrowIfNeeded(int needed)
        {
            if (_usingPadding)
            {
                if (needed > _sectionRawSize)
                    throw new InvalidOperationException(
                        $"Padding cave exhausted: need {needed} bytes, have {_sectionRawSize}.");
                return;
            }
            while (needed > _sectionRawSize)
                ExtendSection(PageSize);
        }

        private void ExtendSection(int extra)
        {
            extra = AlignUp(extra, (int)_fileAlign);
            int newRaw = _sectionRawSize + extra;
            int newVirt = AlignUp(newRaw, (int)_sectionAlign);

            int end = _sectionRawOffset + newRaw;
            if (end > _peData.Length)
            {
                Array.Resize(ref _peData, end);
            }

            BinaryPrimitives.WriteUInt32LittleEndian(_peData.AsSpan(_sectionHdrOffset + 8, 4), (uint)newVirt);
            BinaryPrimitives.WriteUInt32LittleEndian(_peData.AsSpan(_sectionHdrOffset + 16, 4), (uint)newRaw);
            _sectionRawSize = newRaw;
            _sectionVirtSize = newVirt;
        }

        private void CommitSection()
        {
            int end = _sectionRawOffset + _sectionRawSize;
            if (end > _peData.Length)
                Array.Resize(ref _peData, end);
        }

        private void WriteWatermark()
        {
            int off = _sectionRawOffset;
            BinaryPrimitives.WriteUInt32LittleEndian(_peData.AsSpan(off, 4), (uint)_watermark);
            byte[] tag = new byte[8];
            byte[] encoded = Encoding.ASCII.GetBytes(_versionTag);
            int copyLen = Math.Min(encoded.Length, 7);
            Buffer.BlockCopy(encoded, 0, tag, 0, copyLen);
            Buffer.BlockCopy(tag, 0, _peData, off + 4, 8);
        }

        private void UpdateSizeOfImage()
        {
            if (_usingPadding) return;
            uint rva = BinaryPrimitives.ReadUInt32LittleEndian(_peData.AsSpan(_sectionHdrOffset + 12, 4));
            uint virt = BinaryPrimitives.ReadUInt32LittleEndian(_peData.AsSpan(_sectionHdrOffset + 8, 4));
            uint newSoi = AlignUp(rva + virt, _sectionAlign);
            BinaryPrimitives.WriteUInt32LittleEndian(_peData.AsSpan(_optOffset + 56, 4), newSoi);
        }

        private static int AlignUp(int value, int alignment)
        {
            return (value + alignment - 1) & ~(alignment - 1);
        }

        private static uint AlignUp(uint value, uint alignment)
        {
            return (value + alignment - 1) & ~(alignment - 1);
        }
    }
}
