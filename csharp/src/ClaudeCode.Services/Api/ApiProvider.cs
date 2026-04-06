namespace ClaudeCode.Services.Api;

/// <summary>API providers supported by ClaudeCode.</summary>
public enum ApiProviderType
{
    /// <summary>Direct Anthropic API (api.anthropic.com)</summary>
    Anthropic,
    /// <summary>AWS Bedrock</summary>
    AwsBedrock,
    /// <summary>Google Vertex AI</summary>
    VertexAi,
    /// <summary>Azure AI Foundry</summary>
    AzureFoundry,
}

/// <summary>Configuration resolved from environment variables for a specific provider.</summary>
public record ApiProviderConfig
{
    public required ApiProviderType Provider { get; init; }
    public required string BaseUrl { get; init; }
    public required string ApiKey { get; init; }
    public Dictionary<string, string> ExtraHeaders { get; init; } = [];
}

/// <summary>
/// Detects the API provider from environment variables and creates the appropriate client.
/// </summary>
public static class ApiProviderFactory
{
    /// <summary>
    /// Detects which API provider to use based on environment variables.
    /// Priority: ANTHROPIC_API_KEY > AWS credentials > GOOGLE_APPLICATION_CREDENTIALS > AZURE_API_KEY
    /// </summary>
    public static ApiProviderConfig Detect(string? primaryApiKey = null)
    {
        // 1. Direct Anthropic API
        var anthropicKey = primaryApiKey
            ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrEmpty(anthropicKey))
        {
            var baseUrl = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL")
                ?? ApiConstants.DefaultBaseUrl;
            return new ApiProviderConfig
            {
                Provider = ApiProviderType.Anthropic,
                BaseUrl = baseUrl,
                ApiKey = anthropicKey,
            };
        }

        // 2. AWS Bedrock
        var awsRegion = Environment.GetEnvironmentVariable("AWS_REGION")
            ?? Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION");
        var awsAccessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
        if (!string.IsNullOrEmpty(awsRegion) && !string.IsNullOrEmpty(awsAccessKey))
        {
            return new ApiProviderConfig
            {
                Provider = ApiProviderType.AwsBedrock,
                BaseUrl = $"https://bedrock-runtime.{awsRegion}.amazonaws.com",
                ApiKey = "", // Auth handled by AWS SDK SigV4
                ExtraHeaders = new()
                {
                    ["x-provider"] = "bedrock",
                    ["x-aws-region"] = awsRegion,
                },
            };
        }

        // 3. Google Vertex AI
        var gcpProject = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT")
            ?? Environment.GetEnvironmentVariable("ANTHROPIC_VERTEX_PROJECT_ID");
        var gcpRegion = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_REGION")
            ?? Environment.GetEnvironmentVariable("CLOUD_ML_REGION")
            ?? "us-east5";
        if (!string.IsNullOrEmpty(gcpProject))
        {
            return new ApiProviderConfig
            {
                Provider = ApiProviderType.VertexAi,
                BaseUrl = $"https://{gcpRegion}-aiplatform.googleapis.com/v1/projects/{gcpProject}/locations/{gcpRegion}/publishers/anthropic/models",
                ApiKey = "", // Auth handled by GCP ADC
                ExtraHeaders = new()
                {
                    ["x-provider"] = "vertex",
                    ["x-gcp-project"] = gcpProject,
                    ["x-gcp-region"] = gcpRegion,
                },
            };
        }

        // 4. Azure AI Foundry
        var azureKey = Environment.GetEnvironmentVariable("AZURE_API_KEY");
        var azureEndpoint = Environment.GetEnvironmentVariable("AZURE_ENDPOINT");
        if (!string.IsNullOrEmpty(azureKey) && !string.IsNullOrEmpty(azureEndpoint))
        {
            return new ApiProviderConfig
            {
                Provider = ApiProviderType.AzureFoundry,
                BaseUrl = azureEndpoint.TrimEnd('/'),
                ApiKey = azureKey,
                ExtraHeaders = new()
                {
                    ["x-provider"] = "azure",
                },
            };
        }

        // Fallback: return Anthropic with empty key (will fail at request time)
        return new ApiProviderConfig
        {
            Provider = ApiProviderType.Anthropic,
            BaseUrl = ApiConstants.DefaultBaseUrl,
            ApiKey = "",
        };
    }

    /// <summary>
    /// Creates an <see cref="IAnthropicClient"/> for the detected provider with full authentication.
    /// Direct Anthropic API calls are wrapped in <see cref="RetryingAnthropicClient"/> for
    /// exponential-backoff retry; provider-specific clients handle their own error paths.
    /// </summary>
    public static IAnthropicClient CreateClient(HttpClient httpClient, ApiProviderConfig config, string? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(config);

        return config.Provider switch
        {
            ApiProviderType.AwsBedrock   => CreateBedrockClient(httpClient, config, sessionId),
            ApiProviderType.VertexAi     => CreateVertexClient(httpClient, config, sessionId),
            ApiProviderType.AzureFoundry => CreateAzureClient(httpClient, config, sessionId),

            // Default: direct Anthropic API — wrap in RetryingAnthropicClient for
            // exponential back-off on 429/503/529 responses.
            _ => new RetryingAnthropicClient(
                     new AnthropicClient(httpClient, config.ApiKey, config.BaseUrl, sessionId)),
        };
    }

    // -----------------------------------------------------------------------
    // Private factory helpers
    // -----------------------------------------------------------------------

    private static IAnthropicClient CreateBedrockClient(
        HttpClient httpClient, ApiProviderConfig config, string? sessionId)
    {
        var region = config.ExtraHeaders.GetValueOrDefault("x-aws-region")
                     ?? Environment.GetEnvironmentVariable("AWS_REGION")
                     ?? Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION")
                     ?? "us-east-1";

        var accessKey  = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID")       ?? string.Empty;
        var secretKey  = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY")   ?? string.Empty;
        var sessionTok = Environment.GetEnvironmentVariable("AWS_SESSION_TOKEN");

        if (string.IsNullOrEmpty(accessKey) || string.IsNullOrEmpty(secretKey))
        {
            Console.Error.WriteLine(
                "[provider] AWS_ACCESS_KEY_ID and AWS_SECRET_ACCESS_KEY are required for Bedrock.");
            return new AnthropicClient(httpClient, string.Empty, config.BaseUrl, sessionId);
        }

        return new BedrockAnthropicClient(httpClient, region, accessKey, secretKey, sessionTok, sessionId);
    }

    private static IAnthropicClient CreateVertexClient(
        HttpClient httpClient, ApiProviderConfig config, string? sessionId)
    {
        var project = config.ExtraHeaders.GetValueOrDefault("x-gcp-project")
                      ?? Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT")
                      ?? Environment.GetEnvironmentVariable("ANTHROPIC_VERTEX_PROJECT_ID")
                      ?? string.Empty;

        var region = config.ExtraHeaders.GetValueOrDefault("x-gcp-region") ?? "us-east5";

        if (string.IsNullOrEmpty(project))
        {
            Console.Error.WriteLine(
                "[provider] GOOGLE_CLOUD_PROJECT or ANTHROPIC_VERTEX_PROJECT_ID is required for Vertex AI.");
            return new AnthropicClient(httpClient, string.Empty, config.BaseUrl, sessionId);
        }

        var accessToken = VertexAnthropicClient.ObtainAccessToken();
        if (accessToken is null)
        {
            Console.Error.WriteLine(
                "[provider] Could not obtain Vertex AI access token. " +
                "Set VERTEX_ACCESS_TOKEN or ensure gcloud is authenticated.");
            return new AnthropicClient(httpClient, string.Empty, config.BaseUrl, sessionId);
        }

        return new VertexAnthropicClient(httpClient, project, region, accessToken, sessionId);
    }

    private static IAnthropicClient CreateAzureClient(
        HttpClient httpClient, ApiProviderConfig config, string? sessionId)
    {
        var bearerToken = Environment.GetEnvironmentVariable("AZURE_BEARER_TOKEN");
        var apiKey      = string.IsNullOrEmpty(config.ApiKey) ? null : config.ApiKey;

        return new AzureAnthropicClient(
            httpClient,
            endpoint:     config.BaseUrl,
            apiKey:       apiKey,
            bearerToken:  string.IsNullOrEmpty(bearerToken) ? null : bearerToken);
    }
}
