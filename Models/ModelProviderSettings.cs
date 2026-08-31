namespace Timenotifier.Models;

public enum ModelQualityMode
{
    LowCost,
    Balanced,
    HighQuality
}

public enum ModelConnectionTestKind
{
    Analysis,
    ImageGeneration
}

public sealed record ModelConnectionTestResult(
    bool Succeeded,
    string Message,
    string EndpointUsed,
    bool FallbackUsed);

public sealed class SingleModelProviderSettings
{
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(Model);

    public SingleModelProviderSettings Clean()
    {
        return new SingleModelProviderSettings
        {
            BaseUrl = BaseUrl.Trim(),
            ApiKey = ApiKey.Trim(),
            Model = Model.Trim()
        };
    }
}

public sealed class ModelProviderSettings
{
    public SingleModelProviderSettings Analysis { get; set; } = new();
    public SingleModelProviderSettings ImageGeneration { get; set; } = new();
    public bool UseVision { get; set; } = true;
    public ModelQualityMode QualityMode { get; set; } = ModelQualityMode.Balanced;

    public SingleModelProviderSettings Text
    {
        get => Analysis;
        set => Analysis = value;
    }

    public SingleModelProviderSettings Vision
    {
        get => ImageGeneration;
        set => ImageGeneration = value;
    }

    public string BaseUrl
    {
        get => Analysis.BaseUrl;
        set => Analysis.BaseUrl = value;
    }

    public string ApiKey
    {
        get => Analysis.ApiKey;
        set => Analysis.ApiKey = value;
    }

    public string Model
    {
        get => Analysis.Model;
        set => Analysis.Model = value;
    }

    public string TextModel
    {
        get => Analysis.Model;
        set => Analysis.Model = value;
    }

    public string VisionModel
    {
        get => ImageGeneration.Model;
        set => ImageGeneration.Model = value;
    }

    public string EffectiveTextModel => Analysis.Model;
    public string EffectiveVisionModel => Analysis.Model;

    public bool IsConfigured => Analysis.IsConfigured;

    public bool IsVisionConfigured => UseVision && Analysis.IsConfigured;

    public ModelProviderSettings Clean()
    {
        return new ModelProviderSettings
        {
            Analysis = Analysis.Clean(),
            ImageGeneration = ImageGeneration.Clean(),
            UseVision = UseVision,
            QualityMode = QualityMode
        };
    }
}
