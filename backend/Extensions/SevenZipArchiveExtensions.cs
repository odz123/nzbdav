using SharpCompress.Archives.SevenZip;

namespace NzbWebDAV.Extensions;

public static class SevenZipArchiveExtensions
{
    public static long GetEntryStartByteOffset(this SevenZipArchive archive, int index)
    {
        var database = archive?.GetReflectionField("_database");
        var dataStartPosition = (long?)database?.GetReflectionField("_dataStartPosition");
        var packStreamStartPositions = (List<long>?)database?.GetReflectionField("_packStreamStartPositions");

        if (dataStartPosition == null)
            throw new InvalidOperationException("Failed to extract 7zip archive metadata: _dataStartPosition not found");
        if (packStreamStartPositions == null)
            throw new InvalidOperationException("Failed to extract 7zip archive metadata: _packStreamStartPositions not found");

        return dataStartPosition.Value + packStreamStartPositions[index];
    }

    public static long GetPackSize(this SevenZipArchive archive, int index)
    {
        var database = archive?.GetReflectionField("_database");
        var packSizes = (List<long>?)database?.GetReflectionField("_packSizes");

        if (packSizes == null)
            throw new InvalidOperationException("Failed to extract 7zip archive metadata: _packSizes not found");

        return packSizes[index];
    }
}