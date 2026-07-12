using System.Buffers.Binary;
using System.Text;

namespace MirrorsEdgeTweaks.Helpers
{
    public sealed class CaveSection
    {
        private const int HeaderSize = 12;
        private const int InitialAlloc = HeaderSize;
        private const int PageSize = 0x1000;
        private const uint SectionChars = 0xE0000020;
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
        private bool _usingTextPadding;

        private int _peOffset;
        private int _coffOffset;
        private int _optOffset;
        private int _numSections;
        private int _sectionsOffset;

        private bool _forceTextPadding;

        private bool _reuse;
        private int _reuseCursor;

        private readonly List<(int SectionOffset, byte[] Data)> _pending = new();

        private CaveSection() { }

        public string VersionTag => _versionTag;

        public static CaveSection Open(byte[] peData, string versionTag = "", bool forceTextPadding = false)
        {
            var cs = new CaveSection
            {
                _peData = peData,
                _versionTag = versionTag,
                _forceTextPadding = forceTextPadding
            };
            cs.ParsePe();
            return cs;
        }

        public uint Alloc(int size, int align = 4)
        {
            EnsureSection();
            // a patch reapplying over its own existing block allocates from a private
            // cursor inside that block without touching the shared watermark so it overwrites
            // only its own region and never a co-resident patch's
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

        // Frees a patch's cave block but ONLY when that block is the topmost allocation
        // i.e. its end equals the watermark, so nothing else was allocated above it.
        // The exact equality test is fail-safe so if another patch sits above
        // (or the footprint is off) it returns the data unchanged rather than risk zeroing a
        // co-resident patch's region
        public static byte[] ReclaimIfTopmost(byte[] peData, string versionTag, bool forceTextPadding,
            uint caveBaseVa, int footprintSize)
        {
            var cave = Open(peData, versionTag, forceTextPadding);
            int baseOff = (int)(caveBaseVa - cave.SectionVa);
            if (baseOff >= InitialAlloc && baseOff + footprintSize == cave.CurrentWatermark)
            {
                cave.RewindTo(baseOff);
                return cave.Finalize();
            }
            return peData;
        }

        // Lay subsequent Alloc calls over an existing block starting at `offset` (section relative)
        // instead of appending at the watermark. A patch that reapplies its own deterministic cave
        // calls this with its prior base so it rewrites in place
        public void ReuseFrom(int offset)
        {
            EnsureSection();
            if (offset < InitialAlloc || offset > _watermark)
                throw new InvalidOperationException(
                    $"ReuseFrom offset {offset} outside [{InitialAlloc}..{_watermark}].");
            _reuse = true;
            _reuseCursor = offset;
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

        // Frees cave space down to `offset` (section-relative) zero filling the reclaimed range so a
        // reapplied patch reuses the same region instead of growing the cave. A patch that owns the
        // top of the cave calls this with its own base before reallocating
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
            // Header parsing is delegated to the shared PE reader; CaveSection keeps only the
            // raw header offsets it needs for its own section-table edits.
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

            byte[] tagBytes = new byte[8];
            Buffer.BlockCopy(d, _sectionRawOffset + 4, tagBytes, 0, 8);
            int nul = Array.IndexOf(tagBytes, (byte)0);
            _versionTag = Encoding.ASCII.GetString(tagBytes, 0, nul >= 0 ? nul : 8);
        }

        private void EnsureSection()
        {
            if (_sectionExists) return;
            if (!_forceTextPadding && CanAddSection())
                CreateSection();
            else
                UseTextPadding();
        }

        private bool CanAddSection()
        {
            int newHdrOff = _sectionsOffset + _numSections * 40;
            int headersEnd = newHdrOff + 40;
            uint sizeOfHeaders = BinaryPrimitives.ReadUInt32LittleEndian(_peData.AsSpan(_optOffset + 60, 4));
            return headersEnd <= sizeOfHeaders;
        }

        private void UseTextPadding()
        {
            var d = _peData;
            int textHdr = -1;
            for (int i = 0; i < _numSections; i++)
            {
                int hdr = _sectionsOffset + i * 40;
                int nameEnd = 8;
                while (nameEnd > 0 && d[hdr + nameEnd - 1] == 0) nameEnd--;
                if (nameEnd == 5 && d[hdr] == (byte)'.' && d[hdr + 1] == (byte)'t' &&
                    d[hdr + 2] == (byte)'e' && d[hdr + 3] == (byte)'x' && d[hdr + 4] == (byte)'t')
                {
                    textHdr = hdr;
                    break;
                }
            }

            if (textHdr < 0)
                throw new InvalidOperationException("No .text section found for padding fallback.");

            int textRaw = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(textHdr + 20, 4));
            int textRawSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(textHdr + 16, 4));
            uint textRva = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(textHdr + 12, 4));
            int end = textRaw + textRawSize;

            int nulEndScan = end;
            while (nulEndScan > textRaw && d[nulEndScan - 1] == 0)
                nulEndScan--;
            int firstNul = AlignUp(nulEndScan, 16);

            int? existingStart = FindTextCaveHeader(d, textRaw, end);
            if (existingStart.HasValue)
            {
                int caveSize = end - existingStart.Value;
                _sectionExists = true;
                _sectionHdrOffset = textHdr;
                _sectionVa = _imageBase + textRva + (uint)(existingStart.Value - textRaw);
                _sectionRawOffset = existingStart.Value;
                _sectionRawSize = caveSize;
                _sectionVirtSize = caveSize;
                _watermark = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(existingStart.Value, 4));
                if (_watermark < InitialAlloc)
                    _watermark = InitialAlloc;
                byte[] tagBytes = new byte[8];
                Buffer.BlockCopy(d, existingStart.Value + 4, tagBytes, 0, 8);
                int nul = Array.IndexOf(tagBytes, (byte)0);
                _versionTag = Encoding.ASCII.GetString(tagBytes, 0, nul >= 0 ? nul : 8);
                _usingTextPadding = true;
                MakeTextWritable(d, textHdr);
                return;
            }

            int caveSpace = end - firstNul;
            if (caveSpace < 256)
                throw new InvalidOperationException(
                    $"Only {caveSpace} bytes of NUL padding in .text (need at least 256).");

            _sectionExists = true;
            _sectionHdrOffset = textHdr;
            _sectionVa = _imageBase + textRva + (uint)(firstNul - textRaw);
            _sectionRawOffset = firstNul;
            _sectionRawSize = caveSpace;
            _sectionVirtSize = caveSpace;
            _watermark = InitialAlloc;
            _usingTextPadding = true;
            MakeTextWritable(d, textHdr);
        }

        private static void MakeTextWritable(byte[] d, int textHdr)
        {
            uint chars = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(textHdr + 36, 4));
            chars |= 0x80000000;
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(textHdr + 36, 4), chars);
        }

        private static int? FindTextCaveHeader(byte[] d, int textRaw, int textEnd)
        {
            int limit = Math.Max(textRaw, textEnd - 0x2000);
            for (int off = textEnd - 16; off > limit; off -= 16)
            {
                if (off + 12 > d.Length) continue;
                uint wm = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off, 4));
                // wm == HeaderSize is a reclaimed but empty cave - it must still be refound so
                // the next apply reuses this location instead of walking forward past the leftover header
                if (wm < HeaderSize || wm >= textEnd - textRaw) continue;

                byte[] tagBytes = new byte[8];
                Buffer.BlockCopy(d, off + 4, tagBytes, 0, 8);
                int nul = Array.IndexOf(tagBytes, (byte)0);
                string tag = Encoding.ASCII.GetString(tagBytes, 0, nul >= 0 ? nul : 8);
                if (KnownTags.Contains(tag))
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

            int needed = newRawOff + newRawSize;
            if (needed > d.Length)
            {
                Array.Resize(ref d, needed);
                _peData = d;
            }

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
            if (_usingTextPadding)
            {
                if (needed > _sectionRawSize)
                    throw new InvalidOperationException(
                        $"Text padding cave exhausted: need {needed} bytes, have {_sectionRawSize}.");
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
            if (_usingTextPadding) return;
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
