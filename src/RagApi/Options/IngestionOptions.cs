namespace RagApi.Options;

public class IngestionOptions
{
    public bool Enabled { get; set; } = true;
    public string PapersDirectory { get; set; } = "papers";
    public int ChunkSize { get; set; } = 600;
    public int ChunkOverlap { get; set; } = 100;
    public int BatchSize { get; set; } = 10;
    public bool EnableOcr { get; set; } = false;
    public bool EnableVisionCaption { get; set; } = false;
    public string? VisionApiUrl { get; set; }
    public string VisionApiKey { get; set; } = "EMPTY";
    public string VisionModel { get; set; } = "llava";
    public string TessdataPath { get; set; } = "tessdata";
    public int MinVisionImageWidth { get; set; } = 100;
    public int MinVisionImageHeight { get; set; } = 100;
    public int VisionTimeoutSeconds { get; set; } = 60;
    public int MaxVisionImagesPerSlide { get; set; } = 5;
    public int MaxVisionImagesPerFile { get; set; } = 50;

    public void Validate()
    {
        if (ChunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(ChunkSize), "ChunkSize must be > 0");
        if (ChunkOverlap < 0 || ChunkOverlap >= ChunkSize) throw new ArgumentOutOfRangeException(nameof(ChunkOverlap), "ChunkOverlap must be >= 0 and < ChunkSize");
        if (BatchSize <= 0) throw new ArgumentOutOfRangeException(nameof(BatchSize), "BatchSize must be > 0");
        if (MinVisionImageWidth <= 0) throw new ArgumentOutOfRangeException(nameof(MinVisionImageWidth), "MinVisionImageWidth must be > 0");
        if (MinVisionImageHeight <= 0) throw new ArgumentOutOfRangeException(nameof(MinVisionImageHeight), "MinVisionImageHeight must be > 0");
        if (VisionTimeoutSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(VisionTimeoutSeconds), "VisionTimeoutSeconds must be > 0");
        if (MaxVisionImagesPerSlide <= 0) throw new ArgumentOutOfRangeException(nameof(MaxVisionImagesPerSlide), "MaxVisionImagesPerSlide must be > 0");
        if (MaxVisionImagesPerFile <= 0) throw new ArgumentOutOfRangeException(nameof(MaxVisionImagesPerFile), "MaxVisionImagesPerFile must be > 0");
    }
}
