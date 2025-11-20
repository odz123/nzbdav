using Usenet.Nzb;

namespace NzbWebDAV.Models;

public class MultipartFile
{
    public required List<FilePart> FileParts { get; init; }
    public long FileSize => FileParts.Count > 0
        ? FileParts.Last().ByteRange.EndExclusive
        : 0L;

    public class FilePart
    {
        public required NzbFile NzbFile { get; init; }
        public required LongRange ByteRange { get; init; }
        public long PartSize => ByteRange.Count;
    }
}