using System.IO;
using Microsoft.Extensions.Options;

namespace Director.Options;

public sealed class WanGpOptions
{
    public bool Enabled { get; set; } = true;
    public string Endpoint { get; set; } = "http://127.0.0.1:7866/mcp";
    public string GuiUrl { get; set; } = "http://127.0.0.1:7860";
    public string RootPath { get; set; } = string.Empty;
    public string PythonExecutablePath { get; set; } = "python";
    public string OutputDirectory { get; set; } = string.Empty;
    public bool AutoStart { get; set; }
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7866;
    public int PollingIntervalMilliseconds { get; set; } = 750;
    public int StartupTimeoutSeconds { get; set; } = 180;
    public int GenerationTimeoutMinutes { get; set; } = 45;
    public string OutputRootPath { get; set; } = string.Empty;

    public string GetEffectiveOutputRootPath()
    {
        if (!string.IsNullOrWhiteSpace(OutputRootPath))
        {
            return OutputRootPath;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Director",
            "Projects");
    }

    public string GetEffectiveOutputDirectory()
    {
        if (!string.IsNullOrWhiteSpace(OutputDirectory))
        {
            return OutputDirectory;
        }

        return string.IsNullOrWhiteSpace(RootPath)
            ? string.Empty
            : Path.Combine(RootPath, "outputs");
    }
}

public sealed class WanGpOptionsValidator : IValidateOptions<WanGpOptions>
{
    public ValidateOptionsResult Validate(string? name, WanGpOptions options)
    {
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttp ||
            !endpoint.IsLoopback)
        {
            return ValidateOptionsResult.Fail("WanGp:Endpoint must be a localhost HTTP URI.");
        }

        if (!Uri.TryCreate(options.GuiUrl, UriKind.Absolute, out var guiUri) ||
            guiUri.Scheme != Uri.UriSchemeHttp)
        {
            return ValidateOptionsResult.Fail("WanGp:GuiUrl must be a HTTP URI.");
        }

        if (options.Host != "127.0.0.1")
        {
            return ValidateOptionsResult.Fail("WanGp:Host must be 127.0.0.1.");
        }

        if (options.Port is < 1 or > 65535)
        {
            return ValidateOptionsResult.Fail("WanGp:Port must be between 1 and 65535.");
        }

        if (options.AutoStart)
        {
            if (!Directory.Exists(options.RootPath))
            {
                return ValidateOptionsResult.Fail("WanGp:RootPath must be an existing directory.");
            }

            if (!File.Exists(Path.Combine(options.RootPath, "wgp.py")))
            {
                return ValidateOptionsResult.Fail("WanGp:RootPath must contain wgp.py.");
            }

            if (!File.Exists(options.PythonExecutablePath))
            {
                return ValidateOptionsResult.Fail("WanGp:PythonExecutablePath must be an existing file.");
            }
        }

        var outputDirectory = options.GetEffectiveOutputDirectory();
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            try
            {
                var root = Path.GetFullPath(options.RootPath);
                var output = Path.GetFullPath(outputDirectory);
                if (!output.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(output, root, StringComparison.OrdinalIgnoreCase))
                {
                    return ValidateOptionsResult.Fail("WanGp:OutputDirectory must be under WanGp:RootPath.");
                }

                if (!Directory.Exists(output))
                {
                    Directory.CreateDirectory(output);
                }
            }
            catch (Exception ex)
            {
                return ValidateOptionsResult.Fail($"WanGp:OutputDirectory is not usable: {ex.Message}");
            }
        }

        if (options.PollingIntervalMilliseconds < 250)
        {
            return ValidateOptionsResult.Fail("WanGp:PollingIntervalMilliseconds must be at least 250.");
        }

        return ValidateOptionsResult.Success;
    }
}
